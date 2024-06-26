using CTilde.Expr;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde {
	public abstract class LangProvider {
		StringBuilder SB = new StringBuilder();

		protected void Append(string Str) {
			SB.Append(Str);
		}

		protected void Append(string Fmt, params object[] Args) {
			Append(string.Format(Fmt, Args));
		}

		protected void AppendLine(string Str) {
			SB.AppendLine(Str);
		}

		protected void AppendLine(string Fmt, params object[] Args) {
			AppendLine(string.Format(Fmt, Args));
		}

		// Scopes
		public abstract void Compile(Expression Ex);

		public string CompileToSource() {
			return SB.ToString();
		}
	}
}
