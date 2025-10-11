using CTilde.Expr;
using System;
using System.Collections.Generic;
using System.Diagnostics.SymbolStore;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CTilde.FishAsm
{
	public class FishVarDef
	{
		public string Name;
		public int EBPOffset;
		public int Size;

		public FishVarDef(string Name, int EBPOffset, int Size)
		{
			this.Name = Name;
			this.EBPOffset = EBPOffset;
			this.Size = Size;
		}
	}

	public class FishCompileState
	{
		public bool IsInsideFunctionBody = false;
		public bool IsInsideFunctionDef = false;

		public int StackSize;

		List<FishVarDef> VarOffsets = new List<FishVarDef>();
		int ParamCount;
		int ArgCount;

		public int GetTypeSize(Expr_TypeDef Type)
		{
			if (Type.IsPointer || Type.IsArray)
				return 4;

			if (Type.Type == "int" || Type.Type == "uint" || Type.Type == "float" || Type.Type == "bool" || Type.Type == "string")
				return 4;

			throw new NotImplementedException();
		}

		public void ClearVarOffsets()
		{
			VarOffsets.Clear();
			StackSize = 0;
		}

		bool ContainsKey(string Key)
		{
			for (int i = 0; i < VarOffsets.Count; i++)
			{
				if (VarOffsets[i].Name == Key)
					return true;
			}

			return false;
		}

		FishVarDef GetKeyValue(string Key)
		{
			for (int i = 0; i < VarOffsets.Count; i++)
			{
				if (VarOffsets[i].Name == Key)
					return VarOffsets[i];
			}

			return null;
		}

		void SetKeyValue(string Key, int EBPOffset, int Size)
		{
			for (int i = 0; i < VarOffsets.Count; i++)
			{
				if (VarOffsets[i].Name == Key)
				{
					VarOffsets[i].EBPOffset = EBPOffset;
					VarOffsets[i].Size = Size;
					return;
				}
			}

			VarOffsets.Add(new FishVarDef(Key, EBPOffset, Size));
		}

		public void DefineVar(string VarName, int EBPOffset, int Size)
		{
			if (ContainsKey(VarName))
				throw new Exception(string.Format("Variable '{0}' is already defined", VarName));

			SetKeyValue(VarName, EBPOffset, Size);
			StackSize += Size;
		}

		public void DefineVar(string VarName, int Size, bool IsParam)
		{
			if (IsParam)
			{
				DefineVar(VarName, 8 + (ParamCount * 4), Size);
			}
			else
			{
				DefineVar(VarName, -4 - (ArgCount * 4), Size);
			}

			if (IsParam)
				ParamCount++;
			else
				ArgCount++;
		}

		public int GetVarOffset(string VarName)
		{
			if (ContainsKey(VarName))
				return GetKeyValue(VarName).EBPOffset;

			throw new Exception(string.Format("Could not find variable '{0}'", VarName));
		}
	}
}
