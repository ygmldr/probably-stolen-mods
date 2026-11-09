using System.Collections.Generic;
using System.Text;

namespace PawnMind.Core
{
    /// <summary>Human/LLM friendly symbol grid: one character per item, '.' for empty.</summary>
    public static class LayoutDisplay
    {
        private const string Pool = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

        public static Dictionary<int, char> Symbols(IEnumerable<int> ids)
        {
            var symbols = new Dictionary<int, char>();
            if (ids == null) return symbols;

            foreach (int id in ids)
            {
                if (symbols.ContainsKey(id)) continue;
                int index = symbols.Count;
                symbols[id] = index < Pool.Length ? Pool[index] : '?';
            }

            return symbols;
        }

        public static string[] Rows(int[][] rows, IDictionary<int, char> symbols)
        {
            if (rows == null) return new string[0];

            var display = new string[rows.Length];
            for (int y = 0; y < rows.Length; y++)
            {
                int[] row = rows[y];
                var builder = new StringBuilder(row == null ? 0 : row.Length);
                if (row != null)
                {
                    for (int x = 0; x < row.Length; x++)
                    {
                        int id = row[x];
                        if (id == 0)
                        {
                            builder.Append('.');
                        }
                        else
                        {
                            char symbol;
                            builder.Append(symbols != null && symbols.TryGetValue(id, out symbol) ? symbol : '?');
                        }
                    }
                }

                display[y] = builder.ToString();
            }

            return display;
        }
    }
}
