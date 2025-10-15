using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr
{
	public class Expr_ConstChar : Expression
	{
		public char CharLiteral;
		public string RawString;

		public Expr_ConstChar(char CharLiteral, string RawString)
		{
			this.CharLiteral = CharLiteral;
			this.RawString = RawString;
		}

		public override string ToSourceStr()
		{
			return RawString;
		}
	}
}
