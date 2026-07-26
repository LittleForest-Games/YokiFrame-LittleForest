using System;

namespace YokiFrame
{
    /// <summary>
    /// Allocation-free JSON grammar validation for the Local IPC trust
    /// boundary. Semantic field extraction remains owned by the existing
    /// CommandBridge envelope parser.
    /// </summary>
    internal static class LocalIpcJsonSyntax
    {
        private const int MaxNestingDepth = 64;

        internal static bool IsObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return false;

            var parser = new Parser(json);
            return parser.TryParseRootObject();
        }

        private sealed class Parser
        {
            private readonly string mJson;
            private int mIndex;

            internal Parser(string json)
            {
                mJson = json;
            }

            internal bool TryParseRootObject()
            {
                SkipWhitespace();
                if (!ParseObject(0))
                    return false;

                SkipWhitespace();
                return mIndex == mJson.Length;
            }

            private bool ParseValue(int depth)
            {
                if (depth > MaxNestingDepth ||
                    mIndex >= mJson.Length)
                {
                    return false;
                }

                switch (mJson[mIndex])
                {
                    case '{':
                        return ParseObject(depth);
                    case '[':
                        return ParseArray(depth);
                    case '"':
                        return ParseString();
                    case 't':
                        return ParseLiteral("true");
                    case 'f':
                        return ParseLiteral("false");
                    case 'n':
                        return ParseLiteral("null");
                    default:
                        return ParseNumber();
                }
            }

            private bool ParseObject(int depth)
            {
                if (depth > MaxNestingDepth ||
                    !Consume('{'))
                {
                    return false;
                }

                SkipWhitespace();
                if (Consume('}'))
                    return true;

                while (true)
                {
                    if (!ParseString())
                        return false;
                    SkipWhitespace();
                    if (!Consume(':'))
                        return false;
                    SkipWhitespace();
                    if (!ParseValue(depth + 1))
                        return false;
                    SkipWhitespace();
                    if (Consume('}'))
                        return true;
                    if (!Consume(','))
                        return false;
                    SkipWhitespace();
                }
            }

            private bool ParseArray(int depth)
            {
                if (depth > MaxNestingDepth ||
                    !Consume('['))
                {
                    return false;
                }

                SkipWhitespace();
                if (Consume(']'))
                    return true;

                while (true)
                {
                    if (!ParseValue(depth + 1))
                        return false;
                    SkipWhitespace();
                    if (Consume(']'))
                        return true;
                    if (!Consume(','))
                        return false;
                    SkipWhitespace();
                }
            }

            private bool ParseString()
            {
                if (!Consume('"'))
                    return false;

                while (mIndex < mJson.Length)
                {
                    var current = mJson[mIndex++];
                    if (current == '"')
                        return true;
                    if (current < 0x20)
                        return false;
                    if (current != '\\')
                        continue;
                    if (mIndex >= mJson.Length)
                        return false;

                    var escaped = mJson[mIndex++];
                    switch (escaped)
                    {
                        case '"':
                        case '\\':
                        case '/':
                        case 'b':
                        case 'f':
                        case 'n':
                        case 'r':
                        case 't':
                            break;
                        case 'u':
                            for (var digit = 0; digit < 4; digit++)
                            {
                                if (mIndex >= mJson.Length ||
                                    !IsHex(mJson[mIndex++]))
                                {
                                    return false;
                                }
                            }

                            break;
                        default:
                            return false;
                    }
                }

                return false;
            }

            private bool ParseNumber()
            {
                var start = mIndex;
                Consume('-');
                if (mIndex >= mJson.Length)
                    return false;

                if (Consume('0'))
                {
                    if (mIndex < mJson.Length &&
                        IsDigit(mJson[mIndex]))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!IsDigitOneToNine(mJson[mIndex]))
                        return false;
                    mIndex++;
                    while (mIndex < mJson.Length &&
                           IsDigit(mJson[mIndex]))
                    {
                        mIndex++;
                    }
                }

                if (Consume('.'))
                {
                    if (mIndex >= mJson.Length ||
                        !IsDigit(mJson[mIndex]))
                    {
                        return false;
                    }

                    while (mIndex < mJson.Length &&
                           IsDigit(mJson[mIndex]))
                    {
                        mIndex++;
                    }
                }

                if (mIndex < mJson.Length &&
                    (mJson[mIndex] == 'e' ||
                     mJson[mIndex] == 'E'))
                {
                    mIndex++;
                    if (mIndex < mJson.Length &&
                        (mJson[mIndex] == '+' ||
                         mJson[mIndex] == '-'))
                    {
                        mIndex++;
                    }

                    if (mIndex >= mJson.Length ||
                        !IsDigit(mJson[mIndex]))
                    {
                        return false;
                    }

                    while (mIndex < mJson.Length &&
                           IsDigit(mJson[mIndex]))
                    {
                        mIndex++;
                    }
                }

                return mIndex > start;
            }

            private bool ParseLiteral(string literal)
            {
                if (mIndex > mJson.Length - literal.Length)
                    return false;
                if (string.CompareOrdinal(
                        mJson,
                        mIndex,
                        literal,
                        0,
                        literal.Length) != 0)
                {
                    return false;
                }

                mIndex += literal.Length;
                return true;
            }

            private void SkipWhitespace()
            {
                while (mIndex < mJson.Length)
                {
                    var current = mJson[mIndex];
                    if (current != ' ' &&
                        current != '\t' &&
                        current != '\r' &&
                        current != '\n')
                    {
                        return;
                    }

                    mIndex++;
                }
            }

            private bool Consume(char expected)
            {
                if (mIndex >= mJson.Length ||
                    mJson[mIndex] != expected)
                {
                    return false;
                }

                mIndex++;
                return true;
            }

            private static bool IsDigit(char value)
            {
                return value >= '0' && value <= '9';
            }

            private static bool IsDigitOneToNine(char value)
            {
                return value >= '1' && value <= '9';
            }

            private static bool IsHex(char value)
            {
                return IsDigit(value) ||
                       value >= 'a' && value <= 'f' ||
                       value >= 'A' && value <= 'F';
            }
        }
    }
}
