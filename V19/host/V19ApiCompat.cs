using System;
using System.Collections.Generic;
using Siemens.Engineering;

internal static class V19Api
{
    public static string TypeName(object info)
    {
        Type type = CompositionType(info);
        if (type != null)
        {
            return type.FullName;
        }

        if (info == null)
        {
            return "?";
        }

        try
        {
            System.Reflection.PropertyInfo name = info.GetType().GetProperty("Name");
            object value = name == null ? null : name.GetValue(info, null);
            return value == null ? info.ToString() : value.ToString();
        }
        catch
        {
            return info.ToString();
        }
    }

    public static Type CompositionType(object info)
    {
        if (info == null)
        {
            return null;
        }

        try
        {
            System.Reflection.PropertyInfo prop = info.GetType().GetProperty("Type");
            return prop == null ? null : prop.GetValue(info, null) as Type;
        }
        catch
        {
            return null;
        }
    }

    public static IEnumerable<KeyValuePair<string, object>> ReadWriteAttributes(IEngineeringObject target)
    {
        IList<EngineeringAttributeInfo> infos;
        try
        {
            infos = target.GetAttributeInfos();
        }
        catch
        {
            yield break;
        }

        foreach (EngineeringAttributeInfo info in infos)
        {
            object value = null;
            try
            {
                value = target.GetAttribute(info.Name);
            }
            catch
            {
            }

            yield return new KeyValuePair<string, object>(info.Name, value);
        }
    }

    public static void SetName(IEngineeringObject target, string name)
    {
        target.SetAttribute("Name", name);
    }
}
