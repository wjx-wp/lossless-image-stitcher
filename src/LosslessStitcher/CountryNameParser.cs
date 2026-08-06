using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace LosslessStitcher
{
    public static class CountryNameParser
    {
        private static readonly Regex LeadingNumber = new Regex(
            @"^\s*\d+\s*[_\-\. ]+\s*",
            RegexOptions.CultureInvariant);

        private static readonly Regex RepeatedWhitespace = new Regex(
            @"\s+",
            RegexOptions.CultureInvariant);

        public static string FromFileName(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                return String.Empty;
            }

            string original = Path.GetFileNameWithoutExtension(path);
            original = original == null ? String.Empty : original.Trim();
            string caption = LeadingNumber.Replace(original, String.Empty);
            caption = caption.Replace('_', ' ');
            caption = RepeatedWhitespace.Replace(caption, " ").Trim();
            if (caption.Length == 0)
            {
                caption = original;
            }

            if (ShouldConvertToTitleCase(caption))
            {
                TextInfo textInfo = CultureInfo.InvariantCulture.TextInfo;
                caption = textInfo.ToTitleCase(caption.ToLowerInvariant());
            }

            return caption;
        }

        private static bool ShouldConvertToTitleCase(string value)
        {
            bool hasLetter = false;
            bool hasLower = false;
            bool hasUpper = false;
            int letterCount = 0;

            int index;
            for (index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (!Char.IsLetter(character))
                {
                    continue;
                }

                hasLetter = true;
                letterCount++;
                hasLower |= Char.IsLower(character);
                hasUpper |= Char.IsUpper(character);
            }

            if (!hasLetter)
            {
                return false;
            }

            if (hasLower && !hasUpper)
            {
                return true;
            }

            return hasUpper && !hasLower && letterCount > 4;
        }
    }
}
