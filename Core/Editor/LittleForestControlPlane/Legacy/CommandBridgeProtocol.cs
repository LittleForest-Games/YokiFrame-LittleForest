namespace YokiFrame
{
    /// <summary>
    /// Shared identifier validation for the Little Forest control-plane wire.
    /// Native YokiFrame Kit descriptors deliberately do not belong to this
    /// profile; the active dispatcher catalog is built from registered
    /// System and Little Forest extension handlers only.
    /// </summary>
    public static class CommandBridgeProtocol
    {
        public const int MAX_IDENTIFIER_LENGTH = 128;

        public static bool IsSafeIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value)
                || value.Length > MAX_IDENTIFIER_LENGTH
                || value == "."
                || value == "..")
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool isLetter =
                    character >= 'A' && character <= 'Z'
                    || character >= 'a' && character <= 'z';
                bool isDigit =
                    character >= '0' && character <= '9';
                if (isLetter
                    || isDigit
                    || character == '.'
                    || character == '_'
                    || character == '-')
                {
                    continue;
                }

                return false;
            }

            return true;
        }
    }
}
