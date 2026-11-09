using System.Text;

namespace Myrtana.Core
{
    /// <summary>
    /// G3's string-table placeholders: <c>$(name)</c> is replaced by the value of that name
    /// ("$(day). day\n$(hour):$(minute)"). Unknown placeholders stay as they are. The tables write line breaks as the two
    /// characters <c>\n</c>; they become real line breaks.
    /// </summary>
    public static class TextTemplate
    {
        public static string Fill(string template, params (string name, string value)[] values)
        {
            if (string.IsNullOrEmpty(template)) return template;
            var sb = new StringBuilder(template).Replace("\\n", "\n");
            foreach (var (name, value) in values) sb.Replace("$(" + name + ")", value ?? string.Empty);
            return sb.ToString();
        }
    }
}
