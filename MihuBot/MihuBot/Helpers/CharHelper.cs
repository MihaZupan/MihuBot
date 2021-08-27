namespace MihuBot.Helpers
{
    public static class CharHelper
    {
        public static bool TryParseHex(char c1, char c2, out int value)
        {
            value = c1 - '0';
            if ((uint)value <= 9)
            {
                // value is already [0, 9]
            }
            else if ((uint)((value - ('A' - '0')) & ~0x20) <= ('F' - 'A'))
            {
                value = ((value + '0') | 0x20) - 'a' + 10;
            }
            else
            {
                return false;
            }

            int second = c2 - '0';
            if ((uint)second <= 9)
            {
                // second is already [0, 9]
            }
            else if ((uint)((second - ('A' - '0')) & ~0x20) <= ('F' - 'A'))
            {
                second = ((second + '0') | 0x20) - 'a' + 10;
            }
            else
            {
                return false;
            }

            value = (value << 4) + second;
            Debug.Assert(value >= 0 && value <= 255);
            return true;
        }
    }
}
