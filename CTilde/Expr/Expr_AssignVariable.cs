using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr
{
	public class Expr_AssignVariable : Expression
	{
		public Expr_Identifier Variable;
		public Expression AssignmentValue;

		public override Expression Parse(Tokenizer Tok)
		{
			Variable = new Expr_Identifier().Parse<Expr_Identifier>(Tok);

			Tok.NextToken().Assert(Symbol.Assignment);

			AssignmentValue = Expression.ParseExpression(Tok, Symbol.Semicolon);
			return this;
		}
	}
}
