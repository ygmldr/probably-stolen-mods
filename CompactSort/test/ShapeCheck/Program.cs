using System;

internal static class Program
{
    private static void Main()
    {
        var b = new GridShapeBuilder(3, 2);
        b.SetData("111110", 3);
        for (int rot = 0; rot < 4; rot++)
        {
            var s = new GridShapeBuilder(b).SetOrientation(rot).SetPosition(0, 0).Build();
            Console.WriteLine("rot=" + rot + " size=" + s.globalWidth + "x" + s.globalHeight);
            for (int y = 0; y < s.globalHeight; y++)
            {
                for (int x = 0; x < s.globalWidth; x++) Console.Write(s.Get(x, y));
                Console.WriteLine();
            }
        }
    }
}
