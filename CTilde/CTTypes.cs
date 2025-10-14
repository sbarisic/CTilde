using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde
{
	public enum CTTypes
	{
		@string,
		@bool,
		@byte,
		@uint,
		@char,
		@int,
		@float,
		@void,
	}

	public static class CTType
	{
		public static CTTypes Parse(string T)
		{
			return (CTTypes)Enum.Parse(typeof(CTTypes), T);
		}

		public static bool IsUnsigned(string T)
		{
			return IsUnsigned(Parse(T));
		}

		public static bool IsUnsigned(CTTypes T)
		{
			if (T == CTTypes.@string || T == CTTypes.@uint || T == CTTypes.@byte || T == CTTypes.@bool)
				return true;

			return false;
		}
	}
}
