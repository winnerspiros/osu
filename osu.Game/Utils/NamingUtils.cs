// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;

namespace osu.Game.Utils
{
    public static class NamingUtils
    {
        /// <summary>
        /// Given a set of <paramref name="existingNames"/> and a target <paramref name="desiredName"/>,
        /// finds a "best" name closest to <paramref name="desiredName"/> that is not in <paramref name="existingNames"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This helper is most useful in scenarios when creating new objects in a set
        /// (such as adding new difficulties to a beatmap set, or creating a clone of an existing object that needs a unique name).
        /// If <paramref name="desiredName"/> is already present in <paramref name="existingNames"/>,
        /// this method will append the lowest possible number in brackets that doesn't conflict with <paramref name="existingNames"/>
        /// to <paramref name="desiredName"/> and return that.
        /// See <c>osu.Game.Tests.Utils.NamingUtilsTest</c> for concrete examples of behaviour.
        /// </para>
        /// <para>
        /// <paramref name="desiredName"/> and <paramref name="existingNames"/> are compared in a case-insensitive manner,
        /// so this method is safe to use for naming files in a platform-invariant manner.
        /// </para>
        /// </remarks>
        public static string GetNextBestName(IEnumerable<string> existingNames, string desiredName)
        {
            var takenNumbers = new HashSet<int>();

            foreach (string name in existingNames)
            {
                int? number = getNumber(name, desiredName);
                if (number.HasValue)
                    takenNumbers.Add(number.Value);
            }

            return formatResult(takenNumbers, desiredName);
        }

        /// <summary>
        /// Given a set of <paramref name="existingFilenames"/> and a desired target <paramref name="desiredFilename"/>
        /// finds a filename closest to <paramref name="desiredFilename"/> that is not in <paramref name="existingFilenames"/>
        /// </summary>
        public static string GetNextBestFilename(IEnumerable<string> existingFilenames, string desiredFilename)
        {
            string name = Path.GetFileNameWithoutExtension(desiredFilename);
            string extension = Path.GetExtension(desiredFilename);

            var takenNumbers = new HashSet<int>();

            foreach (string filename in existingFilenames)
            {
                if (!filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    continue;

                string nameWithoutExtension = filename.Substring(0, filename.Length - extension.Length);
                int? number = getNumber(nameWithoutExtension, name);

                if (number.HasValue)
                    takenNumbers.Add(number.Value);
            }

            return formatResult(takenNumbers, name, extension);
        }

        private static string formatResult(HashSet<int> takenNumbers, string name, string extension = "")
        {
            int bestNumber = 0;
            while (takenNumbers.Contains(bestNumber))
                bestNumber += 1;

            return bestNumber == 0
                ? $"{name}{extension}"
                : $"{name} ({bestNumber.ToString()}){extension}";
        }

        private static int? getNumber(string candidate, string desiredName)
        {
            if (candidate.Equals(desiredName, StringComparison.OrdinalIgnoreCase))
                return 0;

            if (candidate.Length > desiredName.Length && candidate.StartsWith(desiredName, StringComparison.OrdinalIgnoreCase))
            {
                string suffix = candidate.Substring(desiredName.Length);

                // Check for " (N)" format
                if (suffix.Length >= 4 && suffix.StartsWith(" (", StringComparison.Ordinal) && suffix.EndsWith(')'))
                {
                    string numberPart = suffix.Substring(2, suffix.Length - 3);

                    // Must start with 1-9
                    if (numberPart.Length > 0 && numberPart[0] != '0')
                    {
                        if (int.TryParse(numberPart, out int number))
                        {
                            // Ensure strictly digits
                            bool allDigits = true;
                            foreach (char c in numberPart)
                            {
                                if (!char.IsDigit(c))
                                {
                                    allDigits = false;
                                    break;
                                }
                            }

                            if (allDigits)
                                return number;
                        }
                    }
                }
            }

            return null;
        }
    }
}
