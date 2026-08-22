using System.Windows.Media;

namespace AudioPilot.Controls;

public static class AppMenuGlyphs
{
    public static Geometry Window { get; } = Create("M1.5,2.5 H14.5 V13.5 H1.5 Z M1.5,5.5 H14.5");

    public static Geometry Output { get; } = Create("M1.5,6 H4.5 L8,2.5 V13.5 L4.5,10 H1.5 Z M10,5 C12,7 12,9 10,11 M12,3 C15,6 15,10 12,13");

    public static Geometry Input { get; } = Create("M8,1.5 C6.3,1.5 5,2.8 5,4.5 V8 C5,9.7 6.3,11 8,11 C9.7,11 11,9.7 11,8 V4.5 C11,2.8 9.7,1.5 8,1.5 Z M3,8 C3,11 5,13 8,13 C11,13 13,11 13,8 M8,13 V15 M5,15 H11");

    public static Geometry Routine { get; } = Create("M5,3 L13,8 L5,13 Z");

    public static Geometry Settings { get; } = Create("M2,4 H14 M2,8 H14 M2,12 H14 M5,2 V6 M11,6 V10 M7,10 V14");

    public static Geometry Exit { get; } = Create("M2,1.5 H9 V14.5 H2 Z M7,8 H15 M12,5 L15,8 L12,11");

    public static Geometry Stop { get; } = Create("M3,3 H13 V13 H3 Z");

    public static Geometry SetDefault { get; } = Create("M8,1.5 A6.5,6.5 0 1 1 8,14.5 A6.5,6.5 0 1 1 8,1.5 M8,4.5 A3.5,3.5 0 1 1 8,11.5 A3.5,3.5 0 1 1 8,4.5 M8,7 V9 M7,8 H9");

    public static Geometry Undo { get; } = Create("M6,3.5 L2.5,7 L6,10.5 M3,7 H9.5 C12,7 13.5,8.5 13.5,11.5");

    public static Geometry Redo { get; } = Create("M10,3.5 L13.5,7 L10,10.5 M13,7 H6.5 C4,7 2.5,8.5 2.5,11.5");

    public static Geometry Cut { get; } = Create("M4,2.5 A2,2 0 1 1 4,6.5 A2,2 0 1 1 4,2.5 M4,9.5 A2,2 0 1 1 4,13.5 A2,2 0 1 1 4,9.5 M5.7,5.5 L13.5,12.5 M5.7,10.5 L13.5,3.5");

    public static Geometry Copy { get; } = Create("M5,5 H14 V14 H5 Z M2,2 H11 V5 M2,2 V11 H5");

    public static Geometry Paste { get; } = Create("M5,3 H3 V14 H13 V3 H11 M6,1.5 H10 V4.5 H6 Z M6,8 H10 M6,11 H10");

    public static Geometry Delete { get; } = Create("M3,4 H13 M6,4 V2 H10 V4 M4.5,4 L5,14 H11 L11.5,4 M7,6.5 V11.5 M9,6.5 V11.5");

    public static Geometry SelectAll { get; } = Create("M2,6 V2 H6 M10,2 H14 V6 M14,10 V14 H10 M6,14 H2 V10 M5,8 L7,10 L11,6");

    public static Geometry Duplicate { get; } = Create("M5,5 H14 V14 H5 Z M2,2 H11 V5 M2,2 V11 H5 M9.5,7 V11.5 M7.25,9.25 H11.75");

    private static Geometry Create(string data)
    {
        Geometry geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
