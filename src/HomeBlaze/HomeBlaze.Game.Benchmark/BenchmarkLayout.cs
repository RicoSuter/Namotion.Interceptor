namespace HomeBlaze.Game.Benchmark;

/// <summary>
/// The demo world's walls, doors and trees, as the copies <see cref="BenchmarkWorld"/> tiles; a copy of the layout of
/// `HomeBlaze/Data/Files/Demo/Game.json` without its animals, whose schedules would add work the benchmark does not
/// control.
/// </summary>
internal static class BenchmarkLayout
{
    /// <summary>
    /// The distance between two copies along x, the width of the layout plus a margin.
    /// </summary>
    public const int CopyWidth = 160;

    /// <summary>
    /// The distance between two copies along y, the depth of the layout plus a margin.
    /// </summary>
    public const int CopyDepth = 100;

    private static readonly Func<int, int, GameObject>[] Objects =
    [
        Wall(-28, 8, 26, 1, 10, WallMaterial.Bricks),
        Door(-2, 8, 6, 1, 10, WallMaterial.Bricks, isOpen: true),
        Wall(4, 8, 24, 1, 10, WallMaterial.Bricks),
        Wall(-28, 47, 56, 1, 10, WallMaterial.Bricks),
        Wall(-28, 9, 1, 38, 10, WallMaterial.Bricks),
        Wall(27, 9, 1, 38, 10, WallMaterial.Bricks),
        Wall(-7, 9, 1, 7, 10, WallMaterial.Concrete),
        Door(-7, 16, 1, 5, 10, WallMaterial.Concrete, isOpen: true),
        Wall(-7, 21, 1, 26, 10, WallMaterial.Concrete),
        Wall(10, 9, 1, 7, 10, WallMaterial.Concrete),
        Door(10, 16, 1, 5, 10, WallMaterial.Concrete, isOpen: true),
        Wall(10, 21, 1, 15, 10, WallMaterial.Concrete),
        Door(10, 36, 1, 4, 10, WallMaterial.Concrete, isOpen: false),
        Wall(10, 40, 1, 7, 10, WallMaterial.Concrete),
        Wall(-27, 29, 7, 1, 10, WallMaterial.Concrete),
        Door(-20, 29, 5, 1, 10, WallMaterial.Concrete, isOpen: false),
        Wall(-15, 29, 8, 1, 10, WallMaterial.Concrete),
        Wall(-6, 29, 6, 1, 10, WallMaterial.Concrete),
        Door(0, 29, 5, 1, 10, WallMaterial.Concrete, isOpen: true),
        Wall(5, 29, 5, 1, 10, WallMaterial.Concrete),
        Wall(11, 29, 16, 1, 10, WallMaterial.Concrete),
        Wall(-48, 12, 12, 1, 9, WallMaterial.Wood),
        Wall(-48, 21, 12, 1, 9, WallMaterial.Wood),
        Wall(-48, 13, 1, 8, 9, WallMaterial.Wood),
        Wall(-37, 13, 1, 2, 9, WallMaterial.Wood),
        Door(-37, 15, 1, 4, 9, WallMaterial.Wood, isOpen: false),
        Wall(-37, 19, 1, 2, 9, WallMaterial.Wood),
        Tree(65, 43, 0, 11, 96),
        Tree(41, -7, 1, 19, 85),
        Tree(55, 52, 2, 103, 116),
        Tree(93, -2, 3, 64, 112),
        Tree(82, 14, 4, 255, 103),
        Tree(94, 41, 5, 255, 88),
        Tree(36, -10, 6, 15, 97),
        Tree(44, 32, 0, 240, 85),
        Tree(37, 3, 1, 322, 107),
        Tree(88, 30, 2, 294, 117),
        Tree(84, 1, 3, 339, 93),
        Tree(67, 24, 4, 357, 109),
        Tree(39, 16, 5, 196, 97),
        Tree(75, 46, 6, 113, 89),
        Tree(88, 53, 0, 33, 82),
        Tree(40, 48, 1, 320, 107),
        Tree(89, -9, 2, 318, 117),
        Tree(49, -10, 3, 277, 89),
        Tree(50, 20, 4, 208, 87),
        Tree(74, 10, 5, 265, 87),
        Tree(59, 5, 6, 67, 107),
        Tree(49, 43, 0, 334, 95),
        Tree(58, 24, 1, 262, 116),
        Tree(81, -8, 2, 250, 120),
        Tree(81, 47, 3, 210, 118),
        Tree(55, 11, 4, 311, 91),
        Tree(76, 51, 5, 17, 118),
        Tree(48, 12, 6, 335, 105),
        Tree(66, -5, 0, 83, 88),
        Tree(50, 2, 1, 123, 105),
        Tree(79, 38, 2, 81, 85),
        Tree(37, 40, 3, 38, 84),
        Tree(14, -23, 0, 227, 93),
        Tree(-27, -23, 2, 35, 88),
        Tree(36, -20, 4, 72, 92),
        Tree(-26, -16, 6, 67, 115),
        Tree(7, 59, 1, 178, 82),
        Tree(-20, 60, 3, 315, 118),
        Tree(10, 52, 5, 185, 109)
    ];

    /// <summary>
    /// The objects of one copy.
    /// </summary>
    public static int ObjectCount => Objects.Length;

    /// <summary>
    /// Creates the object of the layout with the index, shifted by the offset.
    /// </summary>
    public static GameObject Create(int index, int offsetX, int offsetY) => Objects[index](offsetX, offsetY);

    private static Func<int, int, GameObject> Wall(int x, int y, int width, int depth, int height, WallMaterial material) =>
        (offsetX, offsetY) => new Wall { X = x + offsetX, Y = y + offsetY, Width = width, Depth = depth, Height = height, Material = material };

    private static Func<int, int, GameObject> Door(int x, int y, int width, int depth, int height, WallMaterial material, bool isOpen) =>
        (offsetX, offsetY) => new Door
        {
            X = x + offsetX, Y = y + offsetY, Width = width, Depth = depth, Height = height, Material = material, IsOpen = isOpen
        };

    private static Func<int, int, GameObject> Tree(int x, int y, int variant, int rotation, int scale) =>
        (offsetX, offsetY) => new Tree { X = x + offsetX, Y = y + offsetY, Variant = variant, Rotation = rotation, Scale = scale };
}
