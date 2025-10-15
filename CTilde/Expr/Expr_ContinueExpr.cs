using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr
{
	public class Expr_ContinueExpr : Expression
	{
		public Expr_ContinueExpr()
		{
		}

		public override Expression Parse(Tokenizer Tok)
		{
			Tok.NextToken().Assert(Keyword.@continue);
			Tok.NextToken().Assert(Symbol.Semicolon);
			return this;
		}
	}
}
