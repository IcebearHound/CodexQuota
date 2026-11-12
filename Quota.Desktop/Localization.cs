using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;

namespace Quota.Desktop;

public static class L
{
    private static readonly Dictionary<string, string> English = Load();
    public static bool IsEnglish { get; private set; }
    public static string Language => IsEnglish ? "en" : "zh-CN";
    public static event EventHandler? LanguageChanged;
    public static string T(string text) => IsEnglish && English.TryGetValue(text, out var translated) ? translated : text;
    public static string F(string chinese, string english, params object[] values)
        => string.Format(CultureInfo.CurrentCulture, IsEnglish ? english : chinese, values);
    public static void Apply(string language)
    {
        var changed = IsEnglish != (language == "en");
        IsEnglish = language == "en";
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "zh-CN");
        if (System.Windows.Application.Current is { } app)
            foreach (var pair in English) app.Resources["Loc." + pair.Key] = IsEnglish ? pair.Value : pair.Key;
        if (changed) LanguageChanged?.Invoke(null, EventArgs.Empty);
    }
    public static string Error(string message)
    {
        if (!IsEnglish) return message;
        if (English.TryGetValue(message, out var translated)) return translated;
        if (message.Contains("登录") || message.Contains("认证")) return "Authentication needs attention. Sign in again from the account menu.";
        if (message.Contains("超时") || message.Contains("连接") || message.Contains("网络")) return "The connection failed. Check your network and refresh this account.";
        if (message.Contains("目录") || message.Contains("保存")) return "Local files are unavailable. Check access permissions or sync this account again.";
        return "The quota could not be updated. Refresh this account or sign in again.";
    }
    private static Dictionary<string, string> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Quota.Desktop.Strings.en.json");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream ?? throw new FileNotFoundException("Language resource missing"))!;
    }
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension(string key) : DynamicResourceExtension("Loc." + key) { }
