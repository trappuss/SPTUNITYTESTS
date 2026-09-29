"""
COD2EFT - checking tools (no fitting logic here):
  * which objects are EFT reference meshes and which are converted COD parts
  * "Compare to EFT": colour COD parts green and EFT's own meshes magenta in the viewport
  * test poses on the EFT armature (to look at the weights before building the bundle)
  * "sticks out of EFT's body" check: how far each COD vertex is outside EFT's reference meshes
    (EFT gear - vests, rigs, backpacks, helmets - is made to sit on EFT's own body)
"""
import json
import math
import re
import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

EFT_PREFIX = "Base Human"
OUTSIDE_ATTR = "COD2EFT_outside_EFT"
COD_COLOR = (0.35, 0.75, 0.35, 1.0)
EFT_COLOR = (0.85, 0.3, 0.7, 1.0)


# ---------------------------------------------------------------------------------------------
# objects
# ---------------------------------------------------------------------------------------------
def _uses(o, arm):
    return o.parent == arm or any(m.type == "ARMATURE" and m.object == arm for m in o.modifiers)


def converted_meshes(eft):
    """COD parts made by Convert (Head / Upper / Lower) that are bound to this EFT armature."""
    return [o for o in bpy.context.scene.objects if o.type == "MESH" and _uses(o, eft) and
            (o.get("cod2eft_part") or any(c.name.startswith("COD2EFT_") for c in o.users_collection))]


def reference_meshes(eft):
    """EFT's own meshes bound to the armature (the template's head / shirt / pants)."""
    conv = set(converted_meshes(eft))
    return [o for o in bpy.context.scene.objects if o.type == "MESH" and _uses(o, eft) and
            o not in conv and len(o.data.polygons) > 0]


# ---------------------------------------------------------------------------------------------
# Compare to EFT (viewport colours)
# ---------------------------------------------------------------------------------------------
def _shading_spaces():
    for win in bpy.context.window_manager.windows:
        for area in win.screen.areas:
            if area.type == "VIEW_3D":
                for sp in area.spaces:
                    if sp.type == "VIEW_3D":
                        yield sp


def compare_is_on(scene):
    return bool(scene.get("cod2eft_compare"))


def compare_on(scene, eft):
    conv, refs = converted_meshes(eft), reference_meshes(eft)
    for o, col in [(o, COD_COLOR) for o in conv] + [(o, EFT_COLOR) for o in refs]:
        if "cod2eft_prev_color" not in o:
            o["cod2eft_prev_color"] = list(o.color)
            o["cod2eft_prev_hidden"] = o.hide_get()
        o.color = col
        o.hide_set(False)
    prev = []
    for sp in _shading_spaces():
        prev.append([sp.shading.type, sp.shading.color_type])
        if sp.shading.type == "WIREFRAME":
            sp.shading.type = "SOLID"
        sp.shading.color_type = "OBJECT"
    if not compare_is_on(scene):
        scene["cod2eft_compare_prev_shading"] = prev
    scene["cod2eft_compare"] = True
    return len(conv), len(refs)


def compare_off(scene):
    for o in bpy.data.objects:
        if "cod2eft_prev_color" in o:
            o.color = tuple(o["cod2eft_prev_color"])
            try:
                o.hide_set(bool(o.get("cod2eft_prev_hidden", False)))
            except RuntimeError:
                pass
            del o["cod2eft_prev_color"]
            if "cod2eft_prev_hidden" in o:
                del o["cod2eft_prev_hidden"]
    prev = scene.get("cod2eft_compare_prev_shading")
    spaces = list(_shading_spaces())
    if prev:
        for sp, (typ, ct) in zip(spaces, prev):
            sp.shading.type = typ
            sp.shading.color_type = ct
        del scene["cod2eft_compare_prev_shading"]
    scene["cod2eft_compare"] = False


# ---------------------------------------------------------------------------------------------
# test poses (world-space rotations about each bone's joint, parent first).  EFT faces -Y, so
# a rotation of -X degrees about world X swings a limb forward.
# ---------------------------------------------------------------------------------------------
TEST_POSES = {
    # bone, axis, degrees (world axes; EFT faces -Y).  For a bone pointing DOWN (legs) a negative
    # X rotation swings it forward; for a bone pointing UP (spine) a positive one leans forward;
    # arms point forward in EFT's pose, so a negative X rotation raises them.  "TZ" = move the
    # bone down/up by metres.
    "SQUAT": ("Squat", [
        ("Pelvis", "TZ", -0.36), ("LThigh1", "X", -95), ("RThigh1", "X", -95),
        ("LCalf", "X", 120), ("RCalf", "X", 120), ("LFoot", "X", -25), ("RFoot", "X", -25),
        ("Spine1", "X", 25)]),
    "ARMS_UP": ("Arms up", [
        ("LUpperarm", "X", -70), ("RUpperarm", "X", -70), ("LForearm1", "X", -20),
        ("RForearm1", "X", -20)]),
    "ARMS_DOWN": ("Arms down (at the sides)", [
        ("LUpperarm", "X", 75), ("RUpperarm", "X", 75)]),
    "TWIST": ("Upper body twist", [
        ("Spine1", "Z", 15), ("Spine2", "Z", 15), ("Spine3", "Z", 15)]),
    "STRIDE": ("Walking stride", [
        ("LThigh1", "X", -35), ("RThigh1", "X", 25), ("LCalf", "X", 20), ("RCalf", "X", 45)]),
    "LEAN": ("Lean / crouch forward", [
        ("Spine1", "X", 20), ("Spine2", "X", 15), ("Spine3", "X", 10), ("Neck", "X", -25)]),
}


def clear_test_pose(eft):
    for pb in eft.pose.bones:
        pb.matrix_basis = Matrix.Identity(4)
    eft["cod2eft_test_pose"] = ""
    bpy.context.view_layer.update()


def apply_test_pose(eft, key):
    clear_test_pose(eft)
    missing = []
    for bone, axis, deg in TEST_POSES[key][1]:
        pb = eft.pose.bones.get(EFT_PREFIX + bone)
        if pb is None:
            missing.append(bone)
            continue
        M = (eft.matrix_world @ pb.matrix).copy()
        if axis == "TZ":
            M.translation.z += deg
        else:
            h = M.translation.copy()
            M.translation = Vector((0, 0, 0))
            M = Matrix.Rotation(math.radians(deg), 4, axis) @ M
            M.translation = h
        pb.matrix = eft.matrix_world.inverted() @ M
        bpy.context.view_layer.update()
    eft["cod2eft_test_pose"] = key
    return missing


def test_pose_active(eft):
    return bool(eft and eft.get("cod2eft_test_pose"))


# ---------------------------------------------------------------------------------------------
# "sticks out of EFT's body"
# ---------------------------------------------------------------------------------------------
def _bvh(objs):
    verts, polys, off = [], [], 0
    for o in objs:
        mw = o.matrix_world
        verts += [mw @ v.co for v in o.data.vertices]
        polys += [[i + off for i in p.vertices] for p in o.data.polygons]
        off += len(o.data.vertices)
    return BVHTree.FromPolygons(verts, polys) if polys else None


def _dominant_bone(o, names):
    """Index into `names` of the strongest EFT group of every vertex (-1 = none)."""
    gi = {g.index: names.index(g.name) for g in o.vertex_groups if g.name in names}
    dom = np.full(len(o.data.vertices), -1)
    for v in o.data.vertices:
        best = 0.0
        for g in v.groups:
            if g.group in gi and g.weight > best:
                best, dom[v.index] = g.weight, gi[g.group]
    return dom


def _bone_axes(eft, refs):
    """Per EFT bone: (A, B) segment from its joint to its (first) child joint, or (C, None) for
    end bones (head, toes, finger tips) = centre of EFT's own mesh around that bone."""
    W = eft.matrix_world
    joints = {b.name: W @ b.head_local for b in eft.data.bones}
    axes = {}
    for b in eft.data.bones:
        kids = [c for c in b.children if c.name.startswith(EFT_PREFIX)]
        # prefer the child that continues the chain (spine -> spine/neck, palm -> middle finger)
        pref = [c for c in kids if not any(k in c.name for k in ("Collarbone", "Thigh1"))
                and "Digit1" not in c.name] or kids
        if b.name.endswith("Palm"):
            pref = [c for c in kids if "Digit3" in c.name] or pref
        if pref:
            axes[b.name] = (joints[b.name], joints[pref[0].name])
        else:
            axes[b.name] = (joints[b.name], None)
    # end bones: centre of the reference vertices that belong to them
    names = list(axes)
    pts = {n: [] for n in names}
    for o in refs:
        dom = _dominant_bone(o, names)
        mw = o.matrix_world
        for v, d in zip(o.data.vertices, dom):
            if d >= 0 and axes[names[d]][1] is None:
                pts[names[d]].append(mw @ v.co)
    for n in names:
        if axes[n][1] is None:
            if pts[n]:
                c = Vector(np.mean(np.array([tuple(p) for p in pts[n]]), axis=0))
                axes[n] = (c, None)
    return axes


def outside_distances(eft, objs, refs, max_reach=0.4):
    """How far every vertex of each object is outside EFT's own body, in metres (+ = outside,
    - = inside).  Measured along the line from the vertex's bone axis (the EFT bone that moves
    it most) out through the vertex: distance of the vertex minus distance of EFT's reference
    surface on that line.  Rest pose.  Vertices whose line misses EFT's meshes get NaN.
    Returns {object: numpy array}."""
    tree = _bvh(refs)
    if tree is None:
        return {}
    axes = _bone_axes(eft, refs)
    names = list(axes)
    out = {}
    for o in objs:
        dom = _dominant_bone(o, names)
        mw = o.matrix_world
        d = np.full(len(o.data.vertices), np.nan)
        for i, v in enumerate(o.data.vertices):
            if dom[i] < 0:
                continue
            A, B = axes[names[dom[i]]]
            p = mw @ v.co
            if B is not None:
                ab = B - A
                t = max(0.0, min(1.0, (p - A).dot(ab) / max(ab.length_squared, 1e-12)))
                q = A + ab * t
            else:
                q = A
            r = p - q
            L = r.length
            if L < 1e-6:
                continue
            u = r / L
            # all crossings of EFT's surface along the line (clothes can have several layers)
            hits, start, travelled = [], q, 0.0
            while travelled < L + max_reach and len(hits) < 16:
                h = tree.ray_cast(start, u, L + max_reach - travelled)
                if h[0] is None:
                    break
                dist = (h[0] - q).length
                hits.append(dist)
                travelled = dist + 1e-4
                start = q + u * travelled
            if not hits:
                continue
            before = [x for x in hits if x <= L]
            # outside: the outermost EFT layer the line crosses before reaching the vertex;
            # inside: the first EFT surface beyond it
            d[i] = L - (max(before) if before else min(hits))
        out[o] = d
    return out


def outside_report(eft, objs=None, threshold=0.03):
    """Per part: share of vertices more than `threshold` outside EFT's reference body, and the
    largest distance.  Returns (lines, distances)."""
    objs = objs if objs is not None else converted_meshes(eft)
    refs = reference_meshes(eft)
    if not objs or not refs:
        return [], {}
    dists = outside_distances(eft, objs, refs)
    lines = []
    for o, d in sorted(dists.items(), key=lambda kv: kv[0].name):
        ok = d[~np.isnan(d)]
        if not len(ok):
            continue
        share = float((ok > threshold).mean())
        lines.append(f"{o.name}: {share:.0%} of vertices more than {threshold * 100:.0f} cm "
                     f"outside EFT's body (most: {np.percentile(ok, 99) * 100:.1f} cm)")
    return lines, dists


def write_outside_colors(dists, full=0.08):
    """Colour attribute: white = inside/on EFT's body, yellow -> red = sticks out (red at
    `full` metres or more)."""
    for o, d in dists.items():
        me = o.data
        attr = me.color_attributes.get(OUTSIDE_ATTR)
        if attr is None:
            attr = me.color_attributes.new(OUTSIDE_ATTR, "FLOAT_COLOR", "POINT")
        d = np.nan_to_num(d, nan=0.0)
        t = np.clip(d / full, 0.0, 1.0)
        col = np.stack([np.ones_like(t), 1.0 - 0.6 * t - 0.4 * (t > 0.5), 1.0 - t,
                        np.ones_like(t)], axis=1)
        col[d <= 0.005] = (1.0, 1.0, 1.0, 1.0)
        attr.data.foreach_set("color", col.astype(np.float32).ravel())
        me.color_attributes.active_color = attr
        me.update()


def clear_outside_colors(objs):
    n = 0
    for o in objs:
        if o.type == "MESH":
            a = o.data.color_attributes.get(OUTSIDE_ATTR)
            if a is not None:
                o.data.color_attributes.remove(a)
                n += 1
    return n


def show_outside_colors():
    for sp in _shading_spaces():
        if sp.shading.type == "WIREFRAME":
            sp.shading.type = "SOLID"
        sp.shading.color_type = "VERTEX"


# ---------------------------------------------------------------------------------------------
# separate by original COD material
# ---------------------------------------------------------------------------------------------
def _short_mat(name):
    return re.sub(r"\.\d{3}$", "", name or "none")


def split_by_material(objs, log=print):
    """Separate each converted part into one object per ORIGINAL COD material (the face
    attribute 'cod2eft_orig_mat' written by the texture conversion; before that, the material
    slots).  Every piece keeps the armature, weights, UVs and the atlas material, and gets its
    own part name (<Part>_<COD material>) so Convert Textures can give it its own texture set.
    Returns the list of resulting objects."""
    import bmesh
    out = []
    for o in objs:
        me = o.data
        nf = len(me.polygons)
        if "cod2eft_orig_mat" in me.attributes and "cod2eft_orig_mats" in o:
            idx = np.empty(nf, np.int32)
            me.attributes["cod2eft_orig_mat"].data.foreach_get("value", idx)
            names = json.loads(o["cod2eft_orig_mats"])
        else:
            idx = np.empty(nf, np.int32)
            me.polygons.foreach_get("material_index", idx)
            names = [m.name if m else "" for m in me.materials]
        vals = [int(v) for v in np.unique(idx)]
        if len(vals) < 2:
            out.append(o)
            continue
        part = o.get("cod2eft_part") or o.name.rsplit("_", 1)[-1]
        made = []
        for v in vals:
            label = _short_mat(names[v] if v < len(names) else f"mat{v}")
            no = o.copy()
            no.data = me.copy()
            for c in o.users_collection:
                c.objects.link(no)
            if o.hide_get():
                no.hide_set(True)
            bm = bmesh.new()
            bm.from_mesh(no.data)
            bm.faces.ensure_lookup_table()
            keep = idx == v
            kill = [f for f in bm.faces if not keep[f.index]]
            bmesh.ops.delete(bm, geom=kill, context="FACES")
            bm.to_mesh(no.data)
            bm.free()
            # drop material slots this piece no longer uses
            mi = np.empty(len(no.data.polygons), np.int32)
            no.data.polygons.foreach_get("material_index", mi)
            used = sorted(set(int(x) for x in mi))
            if len(used) < len(no.data.materials):
                mats = [no.data.materials[i] for i in used]
                remap = {u: k for k, u in enumerate(used)}
                no.data.materials.clear()
                for m in mats:
                    no.data.materials.append(m)
                no.data.polygons.foreach_set("material_index",
                                             np.array([remap[int(x)] for x in mi], np.int32))
            no.data.update()
            no.name = f"{o.name}_{label}"
            no.data.name = no.name
            no["cod2eft_part"] = f"{part}_{label}"
            made.append(no)
        log(f"{o.name}: separated into {len(made)} object(s) by COD material")
        data = o.data
        bpy.data.objects.remove(o, do_unlink=True)
        if data.users == 0:
            bpy.data.meshes.remove(data)
        out += made
    return out


# ---------------------------------------------------------------------------------------------
# first-person hands
# ---------------------------------------------------------------------------------------------
# EFT's first-person hands (the "hands" bundle a top needs, e.g. Hands_BEAR in
# hands/bear/bear_hands_skin.bundle, SPT 4.1) use the same bone names and hierarchy as the
# third-person body (bone-name hashes = paths from Base HumanPelvis), but only these 40 bones:
FP_BONES = tuple(EFT_PREFIX + s + n for s in "LR" for n in (
    ["Upperarm", "Forearm1", "Forearm2", "Forearm3", "Palm"] +
    [f"Digit{d}{k}" for d in range(1, 6) for k in (1, 2, 3)]))
# ... and reach from the fingertips up to a bit past the shoulder joint: 15 % of the upper-arm
# length above it (measured on Hands_BEAR)
FP_TOP = -0.15


def _clean_texture_state(o):
    """Turn a (copied) part whose textures were converted back into its COD materials + UVs."""
    try:
        from . import cod2eft_textures as TX
    except ImportError:
        import cod2eft_textures as TX
    me = o.data
    if "cod2eft_orig_mats" not in o:
        return True
    if not TX._restore_originals(o):
        return False
    if "COD_original_UV" in me.uv_layers:
        uv = np.empty(len(me.loops) * 2, np.float64)
        me.uv_layers["COD_original_UV"].data.foreach_get("uv", uv)
        me.uv_layers[0].data.foreach_set("uv", uv)
        me.uv_layers.remove(me.uv_layers["COD_original_UV"])
    del o["cod2eft_orig_mats"]
    if "cod2eft_orig_mat" in me.attributes:
        me.attributes.remove(me.attributes["cod2eft_orig_mat"])
    me.update()
    return True


def build_fp_hands(eft, sources, base, log=print):
    """Make '<base>_Hands' for EFT's first-person hands from the arms of the converted Upper
    part(s): the faces whose vertices are weighted mostly (>= 50 %) to the 40 first-person bones
    and lie below EFT's hands cut (FP_TOP), with any weight on other bones (collarbone, spine)
    moved onto that arm's Upperarm.  Returns the new object or None.  It overlaps Upper exactly,
    so it is hidden in the viewport (export still writes it)."""
    import bmesh
    if not sources:
        log("First-person hands: no Upper part to take the arms from")
        return None
    # remove an earlier result (also when it was separated by material since)
    for old in [o for o in bpy.data.objects if o.type == "MESH" and
                str(o.get("cod2eft_part", "")).startswith("Hands") and
                (o.name == f"{base}_Hands" or o.name.startswith(f"{base}_Hands_"))]:
        me_old = old.data
        bpy.data.objects.remove(old, do_unlink=True)
        if me_old.users == 0:
            bpy.data.meshes.remove(me_old)
    mw_e = eft.matrix_world
    J = {b.name: np.array(mw_e @ b.head_local) for b in eft.data.bones}
    if any(b not in J for b in FP_BONES):
        log("First-person hands: the EFT armature lacks some hand bones - skipped")
        return None
    fp_set = set(FP_BONES)
    arm = {}
    for s in "LR":
        u, f = J[EFT_PREFIX + s + "Upperarm"], J[EFT_PREFIX + s + "Forearm1"]
        arm[s] = (u, (f - u) / np.linalg.norm(f - u) ** 2)      # t = (p-u).ax, 0 shoulder 1 elbow
    pieces = []
    for src in sources:
        no = src.copy()
        no.data = src.data.copy()
        bpy.context.scene.collection.objects.link(no)
        if not _clean_texture_state(no):
            log(f"First-person hands: {src.name}'s original COD materials are not in this file "
                "- skipped it")
            bpy.data.objects.remove(no, do_unlink=True)
            continue
        me = no.data
        gname = {g.index: g.name for g in no.vertex_groups}
        nv = len(me.vertices)
        co = np.empty(nv * 3)
        me.vertices.foreach_get("co", co)
        mw = np.array(no.matrix_world)
        co = co.reshape(-1, 3) @ mw[:3, :3].T + mw[:3, 3]
        ok = np.zeros(nv, bool)
        for v in me.vertices:
            wl = wr = other = 0.0
            for g in v.groups:
                n = gname.get(g.group, "")
                if n in fp_set:
                    if n[len(EFT_PREFIX)] == "L":
                        wl += g.weight
                    else:
                        wr += g.weight
                else:
                    other += g.weight
            tot = wl + wr + other
            if tot <= 0:
                continue
            s = "L" if wl >= wr else "R"
            u, ax = arm[s]
            t = float((co[v.index] - u) @ ax)
            if (wl + wr) / tot >= 0.5 and t >= FP_TOP:
                ok[v.index] = True
        bm = bmesh.new()
        bm.from_mesh(me)
        bm.faces.ensure_lookup_table()
        kill = [f for f in bm.faces if not all(ok[x.index] for x in f.verts)]
        keep_n = len(bm.faces) - len(kill)
        bmesh.ops.delete(bm, geom=kill, context="FACES")
        bm.to_mesh(me)
        bm.free()
        if keep_n == 0:
            bpy.data.objects.remove(no, do_unlink=True)
            continue
        # weights on non-hand bones -> that arm's Upperarm (the weights still sum to 1)
        gname = {g.index: g.name for g in no.vertex_groups}
        co = np.empty(len(me.vertices) * 3)
        me.vertices.foreach_get("co", co)
        co = co.reshape(-1, 3) @ mw[:3, :3].T + mw[:3, 3]
        n_moved = 0
        for v in me.vertices:
            other = [(g.group, g.weight) for g in v.groups if gname.get(g.group) not in fp_set]
            if not other:
                continue
            wl = sum(g.weight for g in v.groups if gname.get(g.group, "").startswith(
                EFT_PREFIX + "L") and gname.get(g.group) in fp_set)
            wr = sum(g.weight for g in v.groups if gname.get(g.group, "").startswith(
                EFT_PREFIX + "R") and gname.get(g.group) in fp_set)
            s = "L" if wl >= wr else "R"
            up = no.vertex_groups.get(EFT_PREFIX + s + "Upperarm") or \
                no.vertex_groups.new(name=EFT_PREFIX + s + "Upperarm")
            moved = 0.0
            for gi, w in other:
                no.vertex_groups[gi].remove([v.index])
                moved += w
            up.add([v.index], moved, "ADD")
            n_moved += 1
        # drop material slots no longer used
        mi = np.empty(len(me.polygons), np.int32)
        me.polygons.foreach_get("material_index", mi)
        used = sorted(set(int(x) for x in mi))
        if len(used) < len(me.materials):
            mats = [me.materials[i] for i in used]
            remap = {u_: k for k, u_ in enumerate(used)}
            me.materials.clear()
            for m in mats:
                me.materials.append(m)
            me.polygons.foreach_set("material_index",
                                    np.array([remap[int(x)] for x in mi], np.int32))
        me.update()
        no["cod2eft_hands_moved"] = n_moved
        pieces.append(no)
    if not pieces:
        log("First-person hands: no arm faces found on the Upper part")
        return None
    n_moved = sum(int(p.get("cod2eft_hands_moved", 0)) for p in pieces)
    if len(pieces) > 1:
        with bpy.context.temp_override(active_object=pieces[0], object=pieces[0],
                                       selected_objects=pieces,
                                       selected_editable_objects=pieces):
            bpy.ops.object.join()
    h = pieces[0]
    if "cod2eft_hands_moved" in h:
        del h["cod2eft_hands_moved"]
    for c in list(h.users_collection):
        c.objects.unlink(h)
    coll = bpy.data.collections.get("COD2EFT_Hands") or bpy.data.collections.new("COD2EFT_Hands")
    if coll.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(coll)
    coll.objects.link(h)
    h.name = f"{base}_Hands"
    h.data.name = h.name
    h["cod2eft_part"] = "Hands"
    used = {_short_mat(m.name) for m in h.data.materials if m}
    srcmap = {}
    for src in sources:
        srcmap.update(json.loads(src.get("cod2eft_mat_src", "{}")))
    h["cod2eft_mat_src"] = json.dumps({k: v for k, v in srcmap.items() if k in used})
    h.parent = eft
    h.matrix_parent_inverse = eft.matrix_world.inverted()
    mods = [m for m in h.modifiers if m.type == "ARMATURE"]
    if not mods:
        m = h.modifiers.new("Armature", "ARMATURE")
        m.object = eft
    h.hide_set(True)
    nv = len(h.data.vertices)
    used_g = {x.group for v in h.data.vertices for x in v.groups if x.weight > 0}
    bad = [g.name for g in h.vertex_groups if g.index in used_g and g.name not in fp_set]
    log(f"First-person hands: {h.name} - {nv} vertices, {len(h.data.polygons)} faces, "
        f"materials {', '.join(sorted(used))}; {n_moved} shoulder vertices had collarbone / "
        "spine weight moved onto the upper arm. Hidden in the viewport (it overlaps Upper); "
        "it is exported with the rest" + (f" - WARNING weights left on {bad}" if bad else ""))
    return h



# ---------------------------------------------------------------------------------------------
# Adjust layer: hand corrections with the armature, non-destructive until applied
# ---------------------------------------------------------------------------------------------
# Start: a copy of the EFT armature ("COD2EFT_Adjust", bones disconnected so they can be moved,
# rotated and scaled freely) drives an extra Armature modifier at the TOP of every converted
# mesh's stack, so posing it reshapes the meshes live while the EFT armature and the weights stay
# untouched.  Apply: bakes the adjust pose into the meshes (shape keys included) and removes the
# copy.  Cancel: removes it without changing anything.  Same result as unparenting all bones,
# posing, applying the armature modifier per mesh and re-parenting - in one click each way.
ADJUST_RIG = "COD2EFT_Adjust"
ADJUST_MOD = "COD2EFT_Adjust"


def adjust_rig(scene=None):
    scene = scene or bpy.context.scene
    o = scene.objects.get(ADJUST_RIG)
    return o if o is not None and o.type == "ARMATURE" else None


def adjust_meshes(scene=None):
    scene = scene or bpy.context.scene
    return [o for o in scene.objects if o.type == "MESH" and ADJUST_MOD in o.modifiers]


def _set_mode(obj, mode):
    vl = bpy.context.view_layer
    if bpy.context.object is not None and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    for o in vl.objects:
        if o.select_get():
            o.select_set(False)
    obj.hide_set(False)
    obj.select_set(True)
    vl.objects.active = obj
    if mode != "OBJECT":
        bpy.ops.object.mode_set(mode=mode)


def adjust_start(eft, meshes, restore_pose=None, unparent=False):
    """Adds the adjust layer to `meshes`. Returns the adjust armature.
    unparent: the copy's bones get no parents either, so moving / rotating one bone never
    carries its children along (like unparenting all bones by hand)."""
    if adjust_rig() is not None:
        raise RuntimeError("An adjustment is already in progress - apply or cancel it first")
    if not meshes:
        raise RuntimeError("No converted meshes to adjust")
    if bpy.context.object is not None and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    if test_pose_active(eft):
        clear_test_pose(eft)
    adj = eft.copy()
    adj.data = eft.data.copy()
    adj.name = adj.data.name = ADJUST_RIG
    adj.animation_data_clear()
    for c in eft.users_collection:
        c.objects.link(adj)
    adj.parent = eft.parent
    adj.matrix_world = eft.matrix_world.copy()
    for k in [k for k in adj.keys() if k.startswith("cod2eft")]:
        del adj[k]
    for pb in adj.pose.bones:
        pb.matrix_basis = Matrix()
        for c in list(pb.constraints):
            pb.constraints.remove(c)
    _set_mode(adj, "EDIT")
    for eb in adj.data.edit_bones:
        eb.use_connect = False
    if unparent:
        for eb in adj.data.edit_bones:
            eb.parent = None
    bpy.ops.object.mode_set(mode="OBJECT")
    adj["cod2eft_unparented"] = bool(unparent)
    adj.show_in_front = True
    adj.data.display_type = "OCTAHEDRAL"
    if restore_pose:
        _restore_adjust_pose(adj, restore_pose)
    for o in meshes:
        md = o.modifiers.new(ADJUST_MOD, "ARMATURE")
        md.object = adj
        md.use_vertex_groups = True
        md.use_deform_preserve_volume = False
        o.modifiers.move(len(o.modifiers) - 1, 0)          # first: before the EFT armature
    eft["cod2eft_adjust_hidden"] = not eft.hide_get()
    eft.hide_set(True)
    _set_mode(adj, "POSE")
    return adj


def _lbs(coords, groups, mats, n):
    """Linear blend skinning like Blender's Armature modifier (weights normalised, vertices
    without deforming weights stay put). coords (n,3); groups: list of (vertex idx, weight, M)."""
    acc = np.zeros((n, 3))
    tot = np.zeros(n)
    h = np.c_[coords, np.ones(n)]
    for vi, w, M in groups:
        acc[vi] += w[:, None] * (h[vi] @ M.T)[:, :3]
        tot[vi] += w
    out = coords.copy()
    ok = tot > 0
    out[ok] = acc[ok] / tot[ok, None]
    return out


def _bake_adjust(o, adj):
    me = o.data
    n = len(me.vertices)
    if n == 0:
        return
    Mo, Ma = o.matrix_world, adj.matrix_world
    names = {g.index: g.name for g in o.vertex_groups}
    per = {}
    for v in me.vertices:
        for ge in v.groups:
            if ge.weight > 0 and ge.group in names:
                per.setdefault(ge.group, ([], []))
                per[ge.group][0].append(v.index)
                per[ge.group][1].append(ge.weight)
    groups = []
    for gi, (vi, w) in per.items():
        pb = adj.pose.bones.get(names[gi])
        if pb is None or not pb.bone.use_deform:
            continue
        M = Mo.inverted() @ Ma @ pb.matrix @ pb.bone.matrix_local.inverted() @ Ma.inverted() @ Mo
        if all(abs(M[i][j] - (i == j)) < 1e-9 for i in range(4) for j in range(4)):
            M = Matrix()
        groups.append((np.array(vi), np.array(w), np.array(M)))
    if not groups:
        return
    if me.shape_keys:
        for kb in me.shape_keys.key_blocks:
            co = np.empty(n * 3)
            kb.data.foreach_get("co", co)
            kb.data.foreach_set("co", _lbs(co.reshape(n, 3), groups, None, n).ravel())
        co = np.empty(n * 3)
        me.shape_keys.key_blocks[0].data.foreach_get("co", co)
        me.vertices.foreach_set("co", co)
    else:
        co = np.empty(n * 3)
        me.vertices.foreach_get("co", co)
        me.vertices.foreach_set("co", _lbs(co.reshape(n, 3), groups, None, n).ravel())
    me.update()


def _adjust_end(apply):
    adj = adjust_rig()
    meshes = adjust_meshes()
    if bpy.context.object is not None and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    changed = 0
    if adj is not None and apply:
        # every bone, in armature space: restores the same with or without bone parents (a bone
        # that didn't move must stay put even when its parent did)
        last = {pb.name: [list(r) for r in pb.matrix] for pb in adj.pose.bones}
        last["_space"] = "armature"
        bpy.context.scene["cod2eft_adjust_last"] = json.dumps(last)
        done = set()
        for o in meshes:
            if o.data in done:                     # linked duplicates share their mesh
                continue
            if o.data.users > 1:
                o.data = o.data.copy()             # never change a mesh other objects still use
            _bake_adjust(o, adj)
            done.add(o.data)
            changed += 1
    for o in meshes:
        md = o.modifiers.get(ADJUST_MOD)
        if md is not None:
            o.modifiers.remove(md)
    eft = None
    for o in bpy.context.scene.objects:
        if o.type == "ARMATURE" and "cod2eft_adjust_hidden" in o:
            eft = o
            if o["cod2eft_adjust_hidden"]:
                o.hide_set(False)
            del o["cod2eft_adjust_hidden"]
    if adj is not None:
        data = adj.data
        bpy.data.objects.remove(adj, do_unlink=True)
        if data.users == 0:
            bpy.data.armatures.remove(data)
    if eft is not None:
        _set_mode(eft, "OBJECT")
    return changed


def _restore_adjust_pose(adj, pose):
    if pose.get("_space") != "armature":            # saved by 2.5.0 - 2.5.2: local (basis)
        for n, m in pose.items():
            pb = adj.pose.bones.get(n)
            if pb is not None and isinstance(m, list):
                pb.matrix_basis = Matrix(m)
        return
    depth = {}
    for b in adj.data.bones:
        d, p = 0, b.parent
        while p is not None:
            d, p = d + 1, p.parent
        depth[b.name] = d
    for lvl in sorted(set(depth.values())):         # parents first: a child's matrix needs them
        for n, d in depth.items():
            if d == lvl and n in pose:
                adj.pose.bones[n].matrix = Matrix(pose[n])
        bpy.context.view_layer.update()


def adjust_apply():
    """Bakes the adjust pose into the meshes and removes the adjust layer. Returns mesh count."""
    return _adjust_end(True)


def adjust_cancel():
    return _adjust_end(False)


def adjust_last_pose(scene=None):
    raw = (scene or bpy.context.scene).get("cod2eft_adjust_last")
    try:
        return json.loads(raw) if raw else None
    except ValueError:
        return None
