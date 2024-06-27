using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.Remoting.Contexts;

namespace CTilde {
	public enum Keyword : int {
		@class, __ctor, __dtor,
		@if, @else,
	}

	public enum Symbol : int {
		LParen, 
		RParen, 
		LBrace, 
		RBrace,
		LBracket,
		RBracket,
		Comma, 
		Star, 
		Semicolon,
		Equals,
		Assignment,
		Addition,
		Subtraction
	}

	public class Tokenizer {
		Lexer L;
		Token[] Tokens;
		int Pos;

		public Tokenizer(TextReader Reader) {
			LexerSettings Settings = LexerSettings.Default;

			string[] KeywordNames = Enum.GetNames(typeof(Keyword));
			Settings.Keywords = new Dictionary<string, int>();
			Settings.InlineComments = new string[] { "//" };
			Settings.CommentBegin = "/*";
			Settings.CommentEnd = "*/";
			Settings.StringEscapeChar = '\\';
			Settings.StringQuotes = new char[] { '"' };

			foreach (var Keywd in KeywordNames)
				//if (!Keywd.StartsWith("__"))
				Settings.Keywords.Add(Keywd, (int)(Keyword)Enum.Parse(typeof(Keyword), Keywd));

			Settings.Symbols = new Dictionary<string, int>() {
				{ "(", (int)Symbol.LParen }, { ")", (int)Symbol.RParen },
				{ "{", (int)Symbol.LBrace }, { "}", (int)Symbol.RBrace },
				{ "[", (int)Symbol.LBracket }, { "]", (int)Symbol.RBracket },
				{ "*", (int)Symbol.Star },
				{ "+", (int)Symbol.Addition },
				{ "-", (int)Symbol.Subtraction },
				{ "==", (int)Symbol.Equals },
				{ "=", (int)Symbol.Assignment },
				{ ",", (int)Symbol.Comma },
				{ ";", (int)Symbol.Semicolon }
			};

			L = new Lexer(Reader, Settings);
			Tokens = GetTokenArray();
			Pos = 0;
		}

		public Tokenizer(string Filename) : this(new StringReader(File.ReadAllText(Filename))) {
		}

		IEnumerable<Token> GetTokens() {
			foreach (var T in L) {
				if (T.Type != TokenType.Comment && T.Type != TokenType.WhiteSpace)
					yield return T;
			}
		}

		Token[] GetTokenArray() {
			return GetTokens().ToArray();
		}

		public Token NextToken() {
			if (Pos >= Tokens.Length)
				return null;
			return Tokens[Pos++];
		}

		public Token NextToken(TokenType T) {
			return NextToken().Assert(T);
		}

		public Token Peek(int Fwd = 1) {
			int P = Pos + Fwd - 1;
			if (P >= Tokens.Length)
				return null;
			return Tokens[P];
		}
	}

	public static class TokenizerExtensions {
		public static string Position(this Token T) {
			if (T != null)
				return T.GetPos();

			return "NULL";
		}

		public static bool IsKeyword(this Token T, Keyword K) {
			return T.Type == TokenType.Keyword && T.GetID<Keyword>() == K;
		}

		public static bool IsSymbol(this Token T, Symbol S) {
			return T.Type == TokenType.Symbol && T.GetID<Symbol>() == S;
		}

		public static bool IsTokenType(this Token T, TokenType Type) {
			return T.Type == Type;
		}

		public static bool Is(this Token T, Keyword K) {
			return T.IsKeyword(K);
		}

		public static bool Is(this Token T, Symbol S) {
			return T.IsSymbol(S);
		}

		public static bool Is(this Token T, TokenType Type) {
			return T.IsTokenType(Type);
		}

		public static Token Assert(this Token T, Keyword K) {
			if (!T.IsKeyword(K))
				throw new Exception("Expected keyword " + K + " @ " + T.Position());

			return T;
		}

		public static Token Assert(this Token T, Symbol S) {
			if (!T.IsSymbol(S))
				throw new Exception("Expected symbol " + S + " @ " + T.Position());

			return T;
		}

		public static Token Assert(this Token T, TokenType Typ) {
			if (T.Type != Typ)
				throw new Exception("Expected " + Typ + " @ " + T.Position());

			return T;
		}
	}
}