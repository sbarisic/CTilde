using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr {
	public class Expr_ConstString : Expression {
		public string StringLiteral;
		public string RawString;

		public Expr_ConstString(string StringLiteral) {
			this.StringLiteral = StringLiteral;
			RawString = StringLiteral.Substring(1, StringLiteral.Length - 2).Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");
		}

		public override string ToSourceStr()
		{
			return string.Format("\"{0}\"", RawString.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\t", "\\t").Replace("\"", "\\\""));
		}
	}
}
