using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde {
	public abstract class Expression {
		public abstract string ToC();
		public abstract Expression Parse(Tokenizer Tok);

		public T Parse<T>(Tokenizer Tok) where T : Expression {
			return (T)Parse(Tok);
		}

		public override string ToString() {
			throw new NotImplementedException();
		}
	}

	public class Expr_Module : Expression {
		public List<Expression> Expressions;

		public Expr_Module() {
			Expressions = new List<Expression>();
		}

		public void Add(Expression E) {
			Expressions.Add(E);
		}

		public override string ToC() {
			StringBuilder SB = new StringBuilder();
			foreach (var E in Expressions)
				SB.AppendLine(E.ToC());
			return SB.ToString();
		}

		public override Expression Parse(Tokenizer Tok) {
			while (Tok.Peek() != null) {
				if (Tok.Peek().Is(TokenType.Identifier) && Tok.Peek(2).Is(TokenType.Identifier) && Tok.Peek(3).Is(Symbol.LParen)) {
					Add(new Expr_FuncDef().Parse(Tok));
				} else if (Tok.Peek().Is(Keyword.@class))
					Add(new Expr_ClassDef().Parse(Tok));
				else
					throw new Exception();
			}

			return this;
		}
	}

	public class Expr_Block : Expression {
		public List<Expression> Expressions;

		public Expr_Block() {
			Expressions = new List<Expression>();
		}

		public void Add(Expression E) {
			Expressions.Add(E);
		}

		public override Expression Parse(Tokenizer Tok) {
			Tok.NextToken().Assert(Symbol.LBrace);
			Tok.NextToken().Assert(Symbol.RBrace);
			return this;
		}

		public override string ToC() {
			StringBuilder SB = new StringBuilder();
			SB.AppendLine("{");
			foreach (var E in Expressions)
				SB.AppendLine(E.ToC());
			SB.AppendLine("}");
			return SB.ToString();
		}
	}

	public class Expr_TypeDef : Expression {
		public string Type;
		public bool IsArray, IsPointer;
		
		public override Expression Parse(Tokenizer Tok) {
			Type = Tok.NextToken().Assert(TokenType.Identifier).Text;
			if (Tok.Peek().Is(Symbol.Star)) {
				Tok.NextToken();
				IsPointer = true;
			} else if (Tok.Peek().Is(Symbol.LBracket) && Tok.Peek(2).Is(Symbol.RBracket)) {
				Tok.NextToken();
				Tok.NextToken();
				IsArray = true;
			}
			return this;
		}

		public override string ToC() {
			string T = Type;
			if (IsPointer)
				T += "*";
			else if (IsArray)
				T += "[]";
			return T;
		}

		public static Expr_TypeDef MakeClassRef(string Name) {
			Expr_TypeDef Ref = new Expr_TypeDef();
			Ref.Type = Name;
			Ref.IsPointer = true;
			return Ref;
		}
	}

	public class Expr_VariableDef : Expression {
		public Expr_TypeDef Type;
		public string Name;

		public override Expression Parse(Tokenizer Tok) {
			Type = new Expr_TypeDef().Parse<Expr_TypeDef>(Tok);
			Name = Tok.NextToken().Assert(TokenType.Identifier).Text;
			return this;
		}

		public override string ToC() {
			return string.Format("{0} {1}", Type.ToC(), Name);
		}
	}

	public class Expr_ParamsDef : Expression {
		public List<Expr_VariableDef> Definitions;

		public Expr_ParamsDef() {
			Definitions = new List<Expr_VariableDef>();
		}

		public void Append(Expr_VariableDef Var) {
			Definitions.Add(Var);
		}

		public void Prepend(Expr_VariableDef Var) {
			Definitions.Insert(0, Var);
		}

		public override Expression Parse(Tokenizer Tok) {
			Tok.NextToken().Assert(Symbol.LParen);
			while (!Tok.Peek().IsSymbol(Symbol.RParen)) {
				Append(new Expr_VariableDef().Parse<Expr_VariableDef>(Tok));
				if (!Tok.Peek().IsSymbol(Symbol.RParen))
					Tok.NextToken().Assert(Symbol.Comma);
			}
			Tok.NextToken().Assert(Symbol.RParen);
			return this;
		}

		public override string ToC() {
			return string.Join(", ", Definitions.ToArray().Select((D) => D.ToC()));
		}
	}

	public class Expr_FuncDef : Expression {
		public Expr_VariableDef FuncDef;
		public Expr_ParamsDef Params;
		public Expr_Block Body;

		public override Expression Parse(Tokenizer Tok) {
			FuncDef = new Expr_VariableDef().Parse<Expr_VariableDef>(Tok);
			Params = new Expr_ParamsDef().Parse<Expr_ParamsDef>(Tok);
			Body = new Expr_Block().Parse<Expr_Block>(Tok);
			return this;
		}

		public override string ToC() {
			StringBuilder SB = new StringBuilder();
			SB.AppendFormat("{0}({1}) {2}", FuncDef.ToC(), Params.ToC(), Body.ToC());
			return SB.ToString();
		}
	}

	public class Expr_ClassDef : Expression {
		public string Name;
		public List<Expr_FuncDef> Functions;

		public Expr_ClassDef() {
			Functions = new List<Expr_FuncDef>();
		}

		public override string ToC() {
			StringBuilder SB = new StringBuilder();
			SB.AppendFormat("typedef struct {{ }} {0};\n", Name);

			foreach (var Func in Functions)
				SB.AppendLine(Func.ToC());

			return SB.ToString();
		}

		public override Expression Parse(Tokenizer Tok) {
			Tok.NextToken().Assert(Keyword.@class);
			Name = Tok.NextToken().Assert(TokenType.Identifier).Text;
			Tok.NextToken().Assert(Symbol.LBrace);

			while (!Tok.Peek().Is(Symbol.RBrace)) {
				Expr_FuncDef MemberFunc = new Expr_FuncDef().Parse<Expr_FuncDef>(Tok);
				Expr_VariableDef ThisPtr = new Expr_VariableDef();
				ThisPtr.Name = "this";
				ThisPtr.Type = Expr_TypeDef.MakeClassRef(Name);
				MemberFunc.Params.Prepend(ThisPtr);
				Functions.Add(MemberFunc);
			}

			Tok.NextToken().Assert(Symbol.RBrace);
			return this;
		}
	}
}
