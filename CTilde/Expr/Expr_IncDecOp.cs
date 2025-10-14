using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr {
	public class Expr_IncDecOp : Expression {
		public Expression LExpr;
		public bool Inc;


		public Expr_IncDecOp(bool Inc) {
			this.Inc = Inc;
		}

		public override Expression Parse(Tokenizer Tok) {
			LExpr = Expression.ParseExpression(Tok, Symbol.Semicolon);

			if (Tok.Peek().Is(Symbol.Increment))
			{
				Tok.NextToken().Assert(Symbol.Increment);
				Inc = true;
			} else if (Tok.Peek().Is(Symbol.Decrement))
			{
				Tok.NextToken().Assert(Symbol.Decrement);
				Inc = false;
			}
			else
			{
				throw new Exception("Expected ++ or --");
			}

			return this;
		}
	}
}
