using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Media3D;
using RugWindows;

// Standalone regression checks. Compile with Cloth.cs, WindowsBase.dll and
// PresentationCore.dll; no test runner or package download is needed.
internal static class ClothTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception(message);
    }

    private static void Near(double actual, double expected, double epsilon, string message)
    {
        Check(Math.Abs(actual - expected) <= epsilon, message + ": " + actual + " instead of " + expected);
    }

    private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    private static bool Calm(Cloth cloth)
    {
        return !cloth.IsGrabbing && (cloth.MaxSpeed < 4 || (cloth.MaxFrameMotion < 0.5 && cloth.AverageFrameMotion < 0.04));
    }
    private static void FiniteState(Cloth cloth)
    {
        Check(Finite(cloth.MaxSpeed), "Speed must be finite");
        foreach (Point3D p in cloth.Positions) Check(Finite(p.X) && Finite(p.Y) && Finite(p.Z), "Positions must stay finite");
    }

    public static void Main()
    {
        Cloth cloth = new Cloth(580, 400, 30, 1600, 900);
        cloth.Layout(new Point(800, 450), 0, 0);
        Check(cloth.Count == cloth.Columns * cloth.Rows, "Grid dimensions");
        Check(cloth.Indices.Length == (cloth.Columns - 1) * (cloth.Rows - 1) * 6, "Mesh triangle count");
        Near(cloth.Center.X, 800, 1e-8, "Layout center x");
        Near(cloth.Center.Y, 450, 1e-8, "Layout center y");
        Near(cloth.Angle, 0, 1e-8, "Initial angle");
        Near(cloth.UVs[0].Y, 0, 1e-8, "Texture top");
        Near(cloth.UVs[cloth.Count - 1].Y, 1, 1e-8, "Texture bottom");
        int a = cloth.Indices[0], b = cloth.Indices[1], c = cloth.Indices[2];
        Point3D pa = cloth.Positions[a], pb = cloth.Positions[b], pc = cloth.Positions[c];
        Vector3D ab = new Vector3D(pb.X - pa.X, -(pb.Y - pa.Y), pb.Z - pa.Z);
        Vector3D ac = new Vector3D(pc.X - pa.X, -(pc.Y - pa.Y), pc.Z - pa.Z);
        Check(Vector3D.CrossProduct(ab, ac).Z > 0, "WPF mesh must face the camera after y inversion");
        Point flatProjection = cloth.Project(new Point3D(900, 500, 0));
        Near(flatProjection.X, 900, 1e-8, "Floor projection x");
        Near(flatProjection.Y, 500, 1e-8, "Floor projection y");
        double distance = 1600 * 1.15;
        Point liftedProjection = cloth.Project(new Point3D(900, 500, 120));
        Near(liftedProjection.X, 800 + 100 / (1 - 120 / distance), 1e-8, "Perspective projection");
        Check(cloth.Pick(new Point(800, 450)) >= 0, "Hit inside cloth");
        Check(cloth.Pick(new Point(20, 20)) == -1, "Empty desktop must pass through");
        cloth.Rotate(Math.PI / 3);
        Near(cloth.Center.X, 800, 1e-8, "Rotation preserves center x");
        Near(cloth.Center.Y, 450, 1e-8, "Rotation preserves center y");
        Near(cloth.Angle, Math.PI / 3, 1e-8, "Rotation angle");

        cloth.Layout(new Point(800, 450), 0, 140);
        Stopwatch simulation = Stopwatch.StartNew();
        for (int i = 0; i < 120; i++)
        {
            cloth.Step(1.0 / 60);
            if (i < 3) Check(!Calm(cloth), "Freefall must keep animation awake");
        }
        simulation.Stop();
        FiniteState(cloth);
        double high = 0;
        foreach (Point3D p in cloth.Positions) { Check(p.Z >= -1e-8, "Cloth cannot fall through desktop"); high = Math.Max(high, p.Z); }
        Check(high < 1, "Dropped cloth settles onto the desktop");
        Check(cloth.MaxSpeed < 3, "Settled cloth allows the UI to sleep");

        int vertex = cloth.Count - 1;
        Point start = cloth.Project(cloth.Positions[vertex]);
        cloth.BeginGrab(vertex, start, 120);
        Check(cloth.IsGrabbing, "Grab begins");
        Check(!Calm(cloth), "Beginning a grab must keep animation awake");
        for (int i = 0; i < 45; i++)
        {
            double t = (i + 1) / 45.0;
            cloth.MoveGrab(new Point(start.X - 300 * t, start.Y - 150 * t));
            cloth.Step(1.0 / 60);
            Check(!Calm(cloth), "Dragging must keep animation awake");
        }
        for (int i = 0; i < 20; i++)
        {
            cloth.Step(1.0 / 60);
            Check(!Calm(cloth), "Holding cloth must keep animation awake");
        }
        FiniteState(cloth);
        Check(cloth.Positions[vertex].Z > 100, "Mouse lifts cloth above desktop");
        Point held = cloth.Project(cloth.Positions[vertex]);
        Near(held.X, start.X - 300, 2, "Held vertex follows mouse x through perspective");
        Near(held.Y, start.Y - 150, 2, "Held vertex follows mouse y through perspective");
        cloth.Rotate(0.2);
        cloth.Step(1.0 / 60);
        FiniteState(cloth);
        cloth.EndGrab();
        Check(!cloth.IsGrabbing, "Release ends grab");
        for (int i = 0; i < 180; i++) cloth.Step(1.0 / 60);
        FiniteState(cloth);
        Check(cloth.Positions[vertex].Z < 80, "Released corner falls back down");

        Cloth bumps = new Cloth(220, 160, 12, 800, 600);
        bumps.Ground = delegate(double x, double y)
        {
            double q = (x - 400) * (x - 400) / 2500 + (y - 300) * (y - 300) / 1600;
            return q < 1 ? 24 * Math.Pow(1 - q, 0.6) : 0;
        };
        bumps.Layout(new Point(400, 300), 0, 65);
        for (int i = 0; i < 100; i++) bumps.Step(1.0 / 60);
        high = 0;
        foreach (Point3D p in bumps.Positions)
        {
            Check(p.Z + 1e-8 >= bumps.Ground(p.X, p.Y), "Height field prevents icon penetration");
            high = Math.Max(high, p.Z);
        }
        Check(high > 10, "Desktop icons make visible bumps in cloth");

        Cloth folded = new Cloth(220, 160, 12, 800, 600);
        folded.Layout(new Point(400, 300), 0, 0);
        // Fold the right half directly over the left, initially closer than the
        // collision thickness. Distant cloth vertices must separate in 3D.
        int row = folded.Rows / 2;
        int low = row * folded.Columns + 2, upper = row * folded.Columns + 9;
        for (int j = 0; j < folded.Rows; j++)
        {
            for (int i = folded.Columns / 2; i < folded.Columns; i++)
            {
                int k = j * folded.Columns + i;
                Point3D p = folded.Positions[k];
                folded.Positions[k] = new Point3D(800 - p.X, p.Y, folded.Spacing * 0.25);
            }
        }
        Check((folded.Positions[low] - folded.Positions[upper]).Length < folded.Spacing * 0.3, "Fixture starts with overlapping cloth layers");
        for (int i = 0; i < 20; i++) folded.Step(1.0 / 60);
        FiniteState(folded);
        Check((folded.Positions[low] - folded.Positions[upper]).Length > folded.Spacing * 0.8, "Self collisions separate folded cloth layers");

        bumps.Step(double.NaN);
        bumps.Step(0);
        bumps.Step(-1);
        bumps.Step(1000);
        FiniteState(bumps);
        bumps.Positions[0] = new Point3D(double.NaN, 0, 0);
        bumps.Step(1.0 / 60);
        FiniteState(bumps);
        Check(bumps.Pick(new Point(double.NaN, 0)) == -1, "Invalid input cannot grab the desktop");
        bumps.BeginGrab(-1, new Point(), 120);
        Check(!bumps.IsGrabbing, "Invalid vertex cannot start grab");

        DenseGroundDiagnostic();
        Console.WriteLine("PASS: " + assertions + " assertions, including 600 dense-icon frames; " + cloth.Count + " vertices; falling simulation " + (simulation.Elapsed.TotalMilliseconds / 120).ToString("F2") + " ms/frame.");
    }

    private static void DenseGroundDiagnostic()
    {
        const int columns = 202, rows = 115;
        double[] field = new double[columns * rows];
        for (int i = 0; i < field.Length; i++) field[i] = 1;
        for (int by = 0; by < 10; by++)
        {
            for (int bx = 0; bx < 9; bx++)
            {
                double cx = 500 + bx * 75, cy = 245 + by * 45, radius = 22;
                int x0 = Math.Max(0, (int)((cx - radius * 2.3) / 8)), x1 = Math.Min(columns - 1, (int)((cx + radius * 2.3) / 8) + 1);
                int y0 = Math.Max(0, (int)((cy - radius * 2.3) / 8)), y1 = Math.Min(rows - 1, (int)((cy + radius * 2.3) / 8) + 1);
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        double dx = (x * 8 - cx) / radius, dy = (y * 8 - cy) / radius, q = dx * dx + dy * dy;
                        if (q < 5) field[y * columns + x] = Math.Max(field[y * columns + x], 1 + 23 * Math.Exp(-q * 1.25));
                    }
                }
            }
        }
        Cloth dense = new Cloth(620, 420, 32, 1600, 900);
        dense.Ground = delegate(double x, double y)
        {
            double fx = x / 8, fy = y / 8;
            if (fx < 0 || fy < 0 || fx >= columns - 1 || fy >= rows - 1) return 1;
            int ix = (int)fx, iy = (int)fy, k = iy * columns + ix;
            double tx = fx - ix, ty = fy - iy;
            return (field[k] * (1 - tx) + field[k + 1] * tx) * (1 - ty) + (field[k + columns] * (1 - tx) + field[k + columns + 1] * tx) * ty;
        };
        dense.Layout(new Point(800, 450), 0, 75);
        Point3D[] frameStart = new Point3D[dense.Count];
        double lastMotion = 0, lastAverage = 0;
        int calmFrames = 0, longestCalmRun = 0;
        for (int frame = 1; frame <= 600; frame++)
        {
            Array.Copy(dense.Positions, frameStart, dense.Count);
            dense.Step(1.0 / 30);
            lastMotion = lastAverage = 0;
            for (int i = 0; i < dense.Count; i++)
            {
                double move = (dense.Positions[i] - frameStart[i]).Length;
                lastMotion = Math.Max(lastMotion, move);
                lastAverage += move / dense.Count;
            }
            Near(dense.MaxFrameMotion, lastMotion, 1e-10, "Cloth reports frame maximum displacement");
            Near(dense.AverageFrameMotion, lastAverage, 1e-10, "Cloth reports frame average displacement");
            if (lastMotion < 0.5 && lastAverage < 0.04) calmFrames++; else calmFrames = 0;
            longestCalmRun = Math.Max(longestCalmRun, calmFrames);
        }
        FiniteState(dense);
        Check(lastMotion < 0.5, "Dense icon field settles without visible frame-to-frame movement");
        Check(longestCalmRun >= 45, "Dense icon field reaches sustained visual calm for sleep");
    }
}
