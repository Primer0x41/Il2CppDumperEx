using System;
using System.Collections.Generic;

namespace Il2CppDumper
{
    public static class BoyerMooreHorspool
    {
        public static IEnumerable<int> Search(this byte[] source, byte[] pattern)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (pattern == null)
            {
                throw new ArgumentNullException(nameof(pattern));
            }

            int valueLength = source.Length;
            int patternLength = pattern.Length;

            if (valueLength == 0 || patternLength == 0 || patternLength > valueLength)
            {
                yield break;
            }

            var badCharacters = new int[256];

            for (var i = 0; i < 256; i++)
            {
                badCharacters[i] = patternLength;
            }

            var lastPatternByte = patternLength - 1;

            for (int i = 0; i < lastPatternByte; i++)
            {
                badCharacters[pattern[i]] = lastPatternByte - i;
            }

            int index = 0;

            while (index <= valueLength - patternLength)
            {
                for (var i = lastPatternByte; source[index + i] == pattern[i]; i--)
                {
                    if (i == 0)
                    {
                        yield return index;
                        break;
                    }
                }

                index += badCharacters[source[index + lastPatternByte]];
            }
        }

        public static IEnumerable<int> Search(this byte[] source, string stringPattern)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (stringPattern == null)
            {
                throw new ArgumentNullException(nameof(stringPattern));
            }

            var pattern = stringPattern.Split(' ');

            int valueLength = source.Length;
            int patternLength = pattern.Length;

            if (valueLength == 0 || patternLength == 0 || patternLength > valueLength)
            {
                yield break;
            }

            var parsedPattern = new short[patternLength];
            for (int p = 0; p < patternLength; p++)
            {
                parsedPattern[p] = (pattern[p] is "?" or "??")
                    ? (short)-1
                    : (short)Convert.ToByte(pattern[p], 16);
            }

            var badCharacters = new int[256];
            var lastPatternByte = patternLength - 1;
            bool lastIsWildcard = parsedPattern[lastPatternByte] == -1;

            for (var i = 0; i < 256; i++)
            {
                badCharacters[i] = lastIsWildcard ? 1 : patternLength;
            }

            if (!lastIsWildcard)
            {
                for (int i = 0; i < lastPatternByte; i++)
                {
                    if (parsedPattern[i] != -1)
                    {
                        badCharacters[parsedPattern[i]] = lastPatternByte - i;
                    }
                }
            }

            int index = 0;

            while (index <= valueLength - patternLength)
            {
                for (var i = lastPatternByte; CheckEqual(source, parsedPattern, index, i); i--)
                {
                    if (i == 0)
                    {
                        yield return index;
                        break;
                    }
                }

                index += badCharacters[source[index + lastPatternByte]];
            }
        }

        private static bool CheckEqual(byte[] source, short[] pattern, int index, int i)
        {
            return pattern[i] == -1 || source[index + i] == (byte)pattern[i];
        }
    }
}
