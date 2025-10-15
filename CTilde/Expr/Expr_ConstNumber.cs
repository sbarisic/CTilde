using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace CTilde.Expr
{
	public class Expr_ConstNumber : Expression
	{
		public string NumberLiteral;

		public Expr_ConstNumber(string NumberLiteral)
		{
			this.NumberLiteral = NumberLiteral;
		}

		public override string ToSourceStr()
		{
			return NumberLiteral;
		}
	}
}
