using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Escapes
{
    public static class EscapeHelper
    {
        /// <summary>
        /// Escapes.dll version
        /// </summary>
        public static Version Version
        {
            get
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                AssemblyName asmName = asm.GetName();
                return asmName.Version;
            }
        }

        private class escItem
        {
            public char EscChar;
            public char RealChar;
            public escItem(char e, char r)
            {
                EscChar = e;
                RealChar = r;
            }
        }

        private static escItem[] table =
        {new escItem('a', '\a'),
         new escItem('b', '\b'),
         new escItem('f', '\f'),
         new escItem('n', '\n'),
         new escItem('r', '\r'),
         new escItem('t', '\t'),
         new escItem('v', '\v'),
        };

        private static bool _HexOnly = false;
        /// <summary>
        /// If HexOnly, just accept/produce hex w/o the \x.
        /// </summary>
        public static bool HexOnly
        {
            get { return _HexOnly; }
            set
            {
                _HexOnly = value;
            }
        }
        
        /// <summary>
        /// Return a string with encoded control character escape sequences.
        /// </summary>
        /// <param name="input">string</param>
        /// <returns>string with control characters</returns>
        public static string Encode(string input)
        {
            string rv = "";
            for (int i=0; i<input.Length; i++)
            {
                if (_HexOnly)
                {
                    string str;
                    if (input.Length >= i + 2)
                    {
                        str = input.Substring(i, 2);
                        i++;
                    }
                    else // input.Length == i+1
                    {
                        str = input.Substring(i, 1) + "0";
                    }
                    rv += EncodeToChar(str);
                }
                else // !_HexOnly
                {
                    char c = input[i];
                    int remLen = input.Length - i;
                    if ((c == '\\') && (remLen >= 2))
                    {
                        string str;
                        if ((input[i + 1] == 'x') || (input[i + 1] == 'X'))
                        {
                            if (remLen >= 4)
                            {
                                str = input.Substring(i, 4).ToLower();
                                i += 3;
                            }
                            else
                            {
                                // bad input, report as is.
                                rv += input.Substring(i, 2);
                                i++;
                                continue;
                            }
                        }
                        else
                        {
                            str = input.Substring(i, 2).ToLower();
                            i++;
                        }
                        rv += EncodeToChar(str);
                    }
                    else rv += input[i].ToString();
                }
            }
            return rv;
        }

        private static char EncodeToChar(string str)
        {
            char rv;
            if (_HexOnly)
            {
                rv = (char)((hexToChar(str[0]) * 16) + hexToChar(str[1]));
            }
            else
            {
                rv = '\x00';
                if (str[0] == '\\')
                {
                    char c = str[1];
                    if (c == 'x')
                    {
                        rv = (char)((hexToChar(str[2]) * 16) + hexToChar(str[3]));
                    }
                    else
                    {
                        int id;
                        for (id = 0; id < table.Length; id++)
                        {
                            if (table[id].EscChar == c)
                            {
                                rv = table[id].RealChar;
                                break;
                            }
                        }
                        if (id == table.Length) rv = c;
                    }
                }
                else rv = str[0];
            }
            return rv;
        }
        private static byte hexToChar(char c)
        {
            if ((c >= '0') && (c <= '9')) return (byte)(c - '0');
            else return (byte)((c - 'a') + 10);
        }

        /// <summary>
        /// (Overloaded) Return a string containing escape sequences substituted for control characters.
        /// </summary>
        /// <param name="str">input string</param>
        /// <returns>string containing escape sequences</returns>
        public static string Decode(string str)
        {
            int len = str.Length;
            byte[] bytes = new byte[len];
            for (int i = 0; i < len; i++) bytes[i] = (byte)str[i];
            return Decode(bytes, len);
        }

        /// <summary>
        /// (Overloaded) Return a string containing escape sequences substituted for control characters.
        /// </summary>
        /// <param name="input">byte array</param>
        /// <param name="id">byte index or length</param>
        /// <param name="len">number of bytes</param>
        /// <returns>string containing escape sequences</returns>
        public static string Decode(byte[] input, int id, int len)
        {
            string rv = "";
            for (int i = id; i < id + len; i++)
            {
                byte b = input[i];
                if (_HexOnly)
                {
                    rv += b.ToString("x2");
                }
                else
                {
                    if ((b >= 0x20) && (b <= 0x7f)) rv += ((char)b).ToString();
                    else
                    {
                        string str = "";
                        foreach (escItem it in table)
                        {
                            if (it.RealChar == (char)b)
                            {
                                str = "\\" + it.EscChar;
                                break;
                            }
                        }
                        if (str == "") rv += "\\x" + b.ToString("x2");
                        else rv += str;
                    }
                }
            }
            return rv;
        }
        public static string Decode(byte[] input, int len)
        {
            return Decode(input, 0, len);
        }
        public static string Decode(byte[] input)
        {
            return Decode(input, input.Length);
        }
    }
}
