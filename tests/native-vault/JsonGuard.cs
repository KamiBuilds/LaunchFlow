using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
namespace NativeUiDriver {
    // Validate only; deserialization stays in PowerShell. Reject duplicate keys before it loses them.
    public sealed class JsonGuard {
        readonly string text; int position;
        JsonGuard(string value) { text=value; }
        static void Fail() { throw new InvalidOperationException("ManifestRejected"); }
        void White() { while(position<text.Length && " \t\r\n".IndexOf(text[position])>=0) position++; }
        char Take() { if(position>=text.Length) Fail(); return text[position++]; }
        bool Eat(char c) { White(); if(position<text.Length && text[position]==c){position++;return true;}return false; }
        string String() {
            White(); if(Take()!='"') Fail(); StringBuilder result=new StringBuilder();
            while(true) {
                char c=Take(); if(c=='"') return result.ToString(); if(c<32) Fail();
                if(c!='\\'){result.Append(c);continue;}
                c=Take(); switch(c) {
                    case '"': case '\\': case '/': result.Append(c); break;
                    case 'b':result.Append('\b');break; case 'f':result.Append('\f');break;
                    case 'n':result.Append('\n');break; case 'r':result.Append('\r');break; case 't':result.Append('\t');break;
                    case 'u':
                        if(position+4>text.Length) Fail(); ushort code;
                        if(!UInt16.TryParse(text.Substring(position,4),NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out code)) Fail();
                        position+=4; result.Append((char)code);break;
                    default:Fail();break;
                }
            }
        }
        void Value(int depth) {
            if(depth>16) Fail(); White(); if(position>=text.Length) Fail(); char c=text[position];
            if(c=='{') {
                position++; var keys=new HashSet<string>(StringComparer.OrdinalIgnoreCase); if(Eat('}'))return;
                do { if(!keys.Add(String()) || !Eat(':')) Fail(); Value(depth+1); if(Eat('}'))return; } while(Eat(',')); Fail();
            } else if(c=='[') {
                position++; if(Eat(']'))return;
                do { Value(depth+1); if(Eat(']'))return; } while(Eat(',')); Fail();
            } else if(c=='"') String();
            else if(c=='t' || c=='f' || c=='n') {
                string literal=c=='t'?"true":c=='f'?"false":"null";
                foreach(char expected in literal) if(Take()!=expected) Fail();
            } else {
                int start=position;
                while(position<text.Length && "-+0123456789.eE".IndexOf(text[position])>=0)position++;
                if(!System.Text.RegularExpressions.Regex.IsMatch(text.Substring(start,position-start),@"\A-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?\z"))Fail();
            }
        }
        public static void Validate(string value) { var parser=new JsonGuard(value);parser.Value(0);parser.White();if(parser.position!=value.Length)Fail(); }
    }
}
