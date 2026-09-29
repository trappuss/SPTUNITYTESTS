// Tag object read by BepInEx ConfigurationManager (F12) by type name and field names (its documented convention:
// plugins copy this class). Only the fields used here are declared.
using System;
using BepInEx.Configuration;

#pragma warning disable 0169, 0414, 0649
internal sealed class ConfigurationManagerAttributes
{
    public Action<ConfigEntryBase> CustomDrawer;
    public bool? HideDefaultButton;
    public bool? HideSettingName;
    public int? Order;
}
