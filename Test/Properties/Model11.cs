﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Test.Properties {
	static class Model11 {
		public static void Main2(string[] Args) {
			string[] Test = new string[] {
				// Vazeci
				"800528864087-23124-1",
				"800528864079-24015-1",
				"800528864095-24023-1",
				"800528864036-24031-1",
				"800528864079-24040-1",
				"800528864001-24058-1",

				// Ne vazeci
				"800128864087-13128-1",
				"800528874079-24115-1",
				"710528864095-22023-1",
				"800328864036-24038-1",
				"800527864079-24045-1",
				"200528864001-23058-1"
			};


			foreach (string Tst in Test) {
				string[] P = Tst.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries).ToArray();

				string P1 = P[0];
				string P2 = P[1];
				string P3 = P[2];

				string P1K = P[0].Last().ToString();
				string P2K = P[1].Last().ToString();

				P1 = new string(P1.Take(P1.Length - 1).ToArray());
				P2 = new string(P2.Take(P2.Length - 1).ToArray());

				CheckPozivNaBroj(P1, P2, P3, P1K, P2K);
			}

			Console.WriteLine();

			while (true) {
				Console.Write(">> ");
				PrintChecksum(Console.ReadLine().Trim());
			}
		}

		static void PrintChecksum(string Str) {
			Console.Write(Str);

			Console.ForegroundColor = ConsoleColor.DarkYellow;

			Console.WriteLine(KontrolniBroj(Str));
			Console.ResetColor();
		}

		static void CheckPozivNaBroj(string P1, string P2, string P3, string P1K, string P2K) {
			string CalcP1K = KontrolniBroj(P1).ToString();
			string CalcP2K = KontrolniBroj(P2).ToString();

			Console.Write(P1);

			if (CalcP1K == P1K)
				Console.ForegroundColor = ConsoleColor.Green;
			else
				Console.ForegroundColor = ConsoleColor.Red;

			Console.Write(CalcP1K);

			Console.ResetColor();
			Console.Write("-{0}", P2);

			if (CalcP2K == P2K)
				Console.ForegroundColor = ConsoleColor.Green;
			else
				Console.ForegroundColor = ConsoleColor.Red;

			Console.Write(CalcP2K);

			Console.ResetColor();
			Console.WriteLine("-{0}", P3);
		}

		static int KontrolniBroj(string s) {
			// MOD11INI

			int Weight = s.Length + 1;
			int Sum = 0;

			for (int i = 0; i < s.Length; i++) {
				int N = int.Parse(s[i].ToString());

				if (N < 0 || N > 9)
					throw new Exception("Expected single digit number [0-9]");

				Sum += Weight * N;
				Weight--;
			}

			if (Sum >= 0) {
				Sum = 11 - (Sum % 11);

				if (Sum > 9)
					Sum = 0;
			}

			return Sum;
		}
	}
}