using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr
{
	public class Expr_StaticValue : Expression
	{
		//public string NumberLiteral;
		public Expr_TypeDef TypeDefExpr;

		public Expr_StaticValue()
		{
		}

		public override Expression Parse(Tokenizer Tok)
		{
			Tok.NextToken().Assert(Keyword.@static);
			TypeDefExpr = (Expr_TypeDef)new Expr_TypeDef().Parse(Tok);
			Tok.NextToken().Assert(Symbol.Semicolon);
			return this;
		}
	}
}
