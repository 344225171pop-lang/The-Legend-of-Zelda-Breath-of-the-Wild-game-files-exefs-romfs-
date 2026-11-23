using System;
using System.Text.RegularExpressions;

namespace BotwExtractor.Ui
{
    // Maps raw extraction exceptions to concise, actionable user-facing text.
    // Kept Android-free and LibHac-free (callers pass the exception type name and
    // message) so the mapping can be unit tested without the Android runtime.
    public static class ErrorMessages
    {
        public static string Describe(string typeName, string message)
        {
            string m = message ?? "";

            // Per-game titlekey missing -> the user must supply a title.keys that
            // contains this title's key. Surface the rights ID so they know which.
            if (m.IndexOf("title key", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string rights = RightsId(m);
                return "缺少该游戏的标题密钥（title key）。请在主界面「title.keys」处选择包含此游戏密钥的文件后重试。"
                    + (rights == null ? "" : " Rights ID: " + rights + "。");
            }

            bool missingKey = typeName == "MissingKeyException"
                || m.IndexOf("MissingKeyException", StringComparison.Ordinal) >= 0;
            if (missingKey
                || m.IndexOf("key area", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("header key", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "主机密钥不完整或版本不匹配：请确认「prod.keys」为完整、与游戏匹配的密钥文件后重试。";
            }

            if (string.IsNullOrWhiteSpace(m))
                return "提取失败，请检查输入文件与密钥后重试。";
            return m;
        }

        // Rights IDs are 32 hex chars (titleId + 16 zeros); titleIds alone are 16.
        static string RightsId(string message)
        {
            var match = Regex.Match(message, "[0-9A-Fa-f]{32}");
            return match.Success ? match.Value.ToUpperInvariant() : null;
        }
    }
}