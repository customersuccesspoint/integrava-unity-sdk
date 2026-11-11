using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Integrava
{
    /// <summary>
    /// Small JSON reader and string escaper. Unity 2021 does not ship System.Text.Json,
    /// and the Help Center tree is too nested for JsonUtility.
    /// </summary>
    public sealed class IntegravaJson
    {
        public enum Kind
        {
            Null,
            Bool,
            Number,
            String,
            Array,
            Object
        }

        public Kind Type;
        public bool BoolValue;
        public double NumberValue;
        public string StringValue;
        public List<IntegravaJson> Items;
        public Dictionary<string, IntegravaJson> Properties;

        public IntegravaJson Get(string key)
        {
            if (Type != Kind.Object || Properties == null || key == null) return null;
            IntegravaJson value;
            if (Properties.TryGetValue(key, out value)) return value;
            return null;
        }

        public string GetString(string key, string fallback)
        {
            var value = Get(key);
            if (value == null) return fallback;
            if (value.Type == Kind.String) return value.StringValue ?? fallback;
            if (value.Type == Kind.Number) return value.NumberValue.ToString(CultureInfo.InvariantCulture);
            if (value.Type == Kind.Bool) return value.BoolValue ? "true" : "false";
            if (value.Type == Kind.Null) return fallback;
            return fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            var value = Get(key);
            if (value == null || value.Type != Kind.Bool) return fallback;
            return value.BoolValue;
        }

        public int GetInt(string key, int fallback)
        {
            var value = Get(key);
            if (value == null) return fallback;
            if (value.Type == Kind.Number) return (int)value.NumberValue;
            if (value.Type == Kind.String)
            {
                int parsed;
                if (int.TryParse(value.StringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }
            return fallback;
        }

        public static IntegravaJson Parse(string text)
        {
            if (text == null) throw new FormatException("JSON text is missing.");
            var parser = new Parser(text);
            var value = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.End) throw new FormatException("Unexpected characters after JSON.");
            return value;
        }

        public static string Quote(string value)
        {
            var builder = new StringBuilder(value == null ? 4 : value.Length + 2);
            builder.Append('"');
            if (value != null)
            {
                for (var i = 0; i < value.Length; i++)
                {
                    var c = value[i];
                    switch (c)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (c < 32)
                            {
                                builder.Append("\\u");
                                builder.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                builder.Append(c);
                            }
                            break;
                    }
                }
            }
            builder.Append('"');
            return builder.ToString();
        }

        /// <summary>Plain text for uGUI. Tags are removed; a few common entities are decoded.</summary>
        public static string StripHtml(string html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var builder = new StringBuilder(html.Length);
            var inTag = false;
            for (var i = 0; i < html.Length; i++)
            {
                var c = html[i];
                if (c == '<')
                {
                    inTag = true;
                    continue;
                }
                if (c == '>')
                {
                    inTag = false;
                    continue;
                }
                if (!inTag) builder.Append(c);
            }
            return DecodeEntities(builder.ToString()).Trim();
        }

        static string DecodeEntities(string text)
        {
            return text
                .Replace("&nbsp;", " ")
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'");
        }

        sealed class Parser
        {
            readonly string _text;
            int _index;

            public Parser(string text)
            {
                _text = text;
            }

            public bool End { get { return _index >= _text.Length; } }

            public void SkipWhitespace()
            {
                while (_index < _text.Length)
                {
                    var c = _text[_index];
                    if (c != ' ' && c != '\n' && c != '\r' && c != '\t') return;
                    _index++;
                }
            }

            public IntegravaJson ParseValue()
            {
                SkipWhitespace();
                if (_index >= _text.Length) throw new FormatException("Unexpected end of JSON.");
                var c = _text[_index];
                if (c == '{') return ParseObject();
                if (c == '[') return ParseArray();
                if (c == '"') return ParseString();
                if (c == 't' || c == 'f') return ParseBool();
                if (c == 'n') return ParseNull();
                return ParseNumber();
            }

            IntegravaJson ParseObject()
            {
                Expect('{');
                var obj = new IntegravaJson
                {
                    Type = Kind.Object,
                    Properties = new Dictionary<string, IntegravaJson>()
                };
                SkipWhitespace();
                if (Peek('}'))
                {
                    _index++;
                    return obj;
                }
                while (true)
                {
                    SkipWhitespace();
                    var keyNode = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    var value = ParseValue();
                    obj.Properties[keyNode.StringValue ?? ""] = value;
                    SkipWhitespace();
                    if (Peek('}'))
                    {
                        _index++;
                        return obj;
                    }
                    Expect(',');
                }
            }

            IntegravaJson ParseArray()
            {
                Expect('[');
                var array = new IntegravaJson
                {
                    Type = Kind.Array,
                    Items = new List<IntegravaJson>()
                };
                SkipWhitespace();
                if (Peek(']'))
                {
                    _index++;
                    return array;
                }
                while (true)
                {
                    array.Items.Add(ParseValue());
                    SkipWhitespace();
                    if (Peek(']'))
                    {
                        _index++;
                        return array;
                    }
                    Expect(',');
                }
            }

            IntegravaJson ParseString()
            {
                Expect('"');
                var builder = new StringBuilder();
                while (_index < _text.Length)
                {
                    var c = _text[_index++];
                    if (c == '"')
                    {
                        return new IntegravaJson { Type = Kind.String, StringValue = builder.ToString() };
                    }
                    if (c == '\\')
                    {
                        if (_index >= _text.Length) throw new FormatException("Bad string escape.");
                        var esc = _text[_index++];
                        switch (esc)
                        {
                            case '"':
                            case '\\':
                            case '/':
                                builder.Append(esc);
                                break;
                            case 'b': builder.Append('\b'); break;
                            case 'f': builder.Append('\f'); break;
                            case 'n': builder.Append('\n'); break;
                            case 'r': builder.Append('\r'); break;
                            case 't': builder.Append('\t'); break;
                            case 'u':
                                if (_index + 4 > _text.Length) throw new FormatException("Bad unicode escape.");
                                var hex = _text.Substring(_index, 4);
                                _index += 4;
                                int code;
                                if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                    throw new FormatException("Bad unicode escape.");
                                builder.Append((char)code);
                                break;
                            default:
                                throw new FormatException("Bad string escape.");
                        }
                    }
                    else
                    {
                        builder.Append(c);
                    }
                }
                throw new FormatException("Unterminated string.");
            }

            IntegravaJson ParseBool()
            {
                if (Match("true")) return new IntegravaJson { Type = Kind.Bool, BoolValue = true };
                if (Match("false")) return new IntegravaJson { Type = Kind.Bool, BoolValue = false };
                throw new FormatException("Expected true or false.");
            }

            IntegravaJson ParseNull()
            {
                if (!Match("null")) throw new FormatException("Expected null.");
                return new IntegravaJson { Type = Kind.Null };
            }

            IntegravaJson ParseNumber()
            {
                var start = _index;
                if (Peek('-')) _index++;
                while (_index < _text.Length && char.IsDigit(_text[_index])) _index++;
                if (Peek('.'))
                {
                    _index++;
                    while (_index < _text.Length && char.IsDigit(_text[_index])) _index++;
                }
                if (Peek('e') || Peek('E'))
                {
                    _index++;
                    if (Peek('+') || Peek('-')) _index++;
                    while (_index < _text.Length && char.IsDigit(_text[_index])) _index++;
                }
                if (start == _index) throw new FormatException("Expected a number.");
                var slice = _text.Substring(start, _index - start);
                double number;
                if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    throw new FormatException("Expected a number.");
                return new IntegravaJson { Type = Kind.Number, NumberValue = number };
            }

            bool Peek(char c)
            {
                return _index < _text.Length && _text[_index] == c;
            }

            void Expect(char c)
            {
                if (_index >= _text.Length || _text[_index] != c)
                    throw new FormatException("Expected '" + c + "'.");
                _index++;
            }

            bool Match(string word)
            {
                if (_index + word.Length > _text.Length) return false;
                if (string.CompareOrdinal(_text, _index, word, 0, word.Length) != 0) return false;
                _index += word.Length;
                return true;
            }
        }
    }
}
