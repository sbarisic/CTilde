using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.Expr
{
	public abstract class Expression
	{
		public virtual Expression Parse(Tokenizer Tok)
		{
			throw new NotImplementedException();
		}

		/*public virtual Expression Parse(Tokenizer Tok, Expression LExpr) {
			throw new NotImplementedException();
		}*/

		public T Parse<T>(Tokenizer Tok) where T : Expression
		{
			return (T)Parse(Tok);
		}

		static Token[] GetDebugTokens(Tokenizer Tok)
		{
			Token[] DebugTokens = new Token[5];

			for (int i = 0; i < 5; i++)
			{
				DebugTokens[i] = Tok.Peek(i + 1);
				Console.WriteLine("{0} - {1}", i, DebugTokens[i]);
			}

			return DebugTokens;
		}

		public static Expression ParseStatement(Tokenizer Tok)
		{
			Token[] DebugTokens = GetDebugTokens(Tok);
			Token PT = Tok.Peek();

			if (Tok.Peek().Is(Keyword.naked))
			{
				Tok.NextToken().Assert(Keyword.naked);

				Expr_FuncDef FDef = (Expr_FuncDef)new Expr_FuncDef().Parse(Tok);
				FDef.Naked = true;

				return FDef;
			}
			else if (Tok.Peek().Is(Keyword.@break))
			{
				Expr_BreakExpr BDef = (Expr_BreakExpr)new Expr_BreakExpr().Parse(Tok);
				return BDef;
			}
			else if (Tok.Peek().Is(Keyword.@while))
			{
				Tok.NextToken().Assert(Keyword.@while);

				return new Expr_WhileStatement().Parse(Tok);
			}
			else if (Tok.Peek().Is(Keyword.@if))
			{
				Tok.NextToken().Assert(Keyword.@if);

				return new Expr_IfElseStatement().Parse(Tok);
			}
			else if (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(Symbol.Star) && Tok.Peek(3).Is(TokenType.Identifier) && Tok.Peek(4).Is(Symbol.LParen))
			{
				// Function definition with pointer return type
				return new Expr_FuncDef().Parse(Tok);
			}
			else if (Tok.Peek().Is(Keyword.__ctor) || Tok.Peek().Is(Keyword.__dtor) || (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(TokenType.Identifier) && Tok.Peek(3).Is(Symbol.LParen)))
			{
				// Function definition
				return new Expr_FuncDef().Parse(Tok);
			}
			else if (Tok.Peek().Is(Keyword.@class))
			{
				// Class definition
				return new Expr_ClassDef().Parse(Tok);
			}
			else if ((Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(TokenType.Identifier) && Tok.Peek(3).Is(Symbol.Semicolon)) || (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(Symbol.Star) && Tok.Peek(3).Is(TokenType.Identifier) && Tok.Peek(4).Is(Symbol.Semicolon)))
			{
				// Variable definition
				Expression Var = new Expr_VariableDef().Parse(Tok);
				Tok.NextToken().Assert(Symbol.Semicolon);

				return Var;
			}
			else if (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(Symbol.Assignment))
			{
				Expression Var = new Expr_AssignVariable().Parse(Tok);

				return Var;
			}
			else if (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(TokenType.Identifier) && Tok.Peek(3).Is(Symbol.Assignment) || (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(Symbol.Star) && Tok.Peek(3).Is(TokenType.Identifier) && Tok.Peek(4).Is(Symbol.Assignment)))
			{
				// Variable definition with expression assignment
				Expression Var = new Expr_AssignedVariableDef().Parse(Tok);

				return Var;
			}
			else if (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(Symbol.LParen))
			{

				// Function call
				Expression Var = new Expr_FuncCall().Parse(Tok);
				Tok.NextToken().Assert(Symbol.Semicolon);

				return Var;
			}

			Expression EE = ParseExpression(Tok, Symbol.Semicolon);
			if (EE is Expr_AssignValue ExprAssignVal)
				return ExprAssignVal;

			// Empty statement
			/*if (Tok.Peek().Is(Symbol.Semicolon))
				return null;*/

			throw new Exception();
		}

		public static Expression ParseExpression(Tokenizer Tok, Symbol StopSymbol)
		{
			Token[] DebugTokens = GetDebugTokens(Tok);

			Expression LeftExpr = null;

			while (!Tok.Peek().Is(StopSymbol))
			{
				if (LeftExpr != null)
				{
					if (Tok.Peek().Is(Symbol.Assignment))
					{
						return new Expr_AssignValue(LeftExpr).Parse<Expr_AssignValue>(Tok);
					}

					if (Tok.Peek().Is(Symbol.Addition) || Tok.Peek().Is(Symbol.Subtraction))
					{
						return new Expr_MathOp(LeftExpr).Parse<Expr_MathOp>(Tok);
					}

					if (Tok.Peek().Is(Symbol.Equals) || Tok.Peek().Is(Symbol.NotEquals) ||
						Tok.Peek().Is(Symbol.GreaterThan) || Tok.Peek().Is(Symbol.LessThan) ||
						Tok.Peek().Is(Symbol.GreaterThanOrEqual) || Tok.Peek().Is(Symbol.LessThanOrEqual))
					{
						return new Expr_ComparisonOp(LeftExpr).Parse<Expr_ComparisonOp>(Tok);
					}

					return LeftExpr;
					//throw new InvalidOperationException("Unexpected token " + Tok.Peek());
				}


				Token PT = Tok.Peek();

				if (Tok.Peek().Is(Symbol.AddressOf) && Tok.Peek(2).Is(TokenType.Identifier))
				{
					LeftExpr = new Expr_AddressOfOp().Parse<Expr_AddressOfOp>(Tok);
				}
				else if (Tok.Peek().Is(Symbol.Star) && Tok.Peek(2).Is(TokenType.Identifier))
				{
					LeftExpr = new Expr_DerefOp().Parse<Expr_DerefOp>(Tok);
				}
				else if (Tok.Peek().Is(Symbol.LParen))
				{
					Tok.NextToken().Assert(Symbol.LParen);
					LeftExpr = Expression.ParseExpression(Tok, Symbol.RParen);
					Tok.NextToken().Assert(Symbol.RParen);
				}
				else if (Tok.Peek().Is(Keyword.@true))
				{
					Tok.NextToken().Assert(Keyword.@true);
					LeftExpr = new Expr_ConstNumber("1");
				}
				else if (Tok.Peek().Is(Keyword.@false))
				{
					Tok.NextToken().Assert(Keyword.@false);
					LeftExpr = new Expr_ConstNumber("0");
				}
				else if (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(Symbol.LBracket))
				{
					LeftExpr = new Expr_IndexOp(new Expr_Identifier().Parse<Expr_Identifier>(Tok)).Parse<Expr_IndexOp>(Tok);
				}
				else if (Tok.Peek().Is(TokenType.Number) || Tok.Peek().Is(TokenType.Decimal))
				{
					LeftExpr = new Expr_ConstNumber(Tok.NextToken().Text);
				}
				else if (Tok.Peek().Is(TokenType.Identifier))
				{
					LeftExpr = new Expr_Identifier().Parse<Expr_Identifier>(Tok);
				}
				else if (Tok.Peek().Is(TokenType.QuotedString))
				{
					PT = Tok.Peek();

					if (PT.Text.StartsWith("'") && PT.Text.EndsWith("'") && PT.Text.Length == 3)
					{
						PT = Tok.NextToken();

						char Chr = PT.Text[1];
						string RawChr = PT.Text;
						LeftExpr = new Expr_ConstChar(Chr, RawChr);
					}
					else if (PT.Text.StartsWith("\"") && PT.Text.EndsWith("\""))
						LeftExpr = new Expr_ConstString(Tok.NextToken().Text);
					else
						throw new NotImplementedException();
				}
				else
					throw new NotImplementedException();
			}

			Tok.NextToken().Assert(StopSymbol);
			return LeftExpr;
		}


		public override string ToString()
		{
			throw new InvalidOperationException();
		}
	}
}
