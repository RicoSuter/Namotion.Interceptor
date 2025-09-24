using Namotion.Interceptor.OpcUa.Server;

namespace Namotion.Interceptor.OpcUa.Tests;

public class ArrayValueRankTests
{
    [Fact]
    public void GetValueRank_ScalarTypes_ShouldReturnMinusOne()
    {
        // Arrange & Act & Assert
        Assert.Equal(-1, GetValueRankTestHelper(typeof(int)));
        Assert.Equal(-1, GetValueRankTestHelper(typeof(string)));
        Assert.Equal(-1, GetValueRankTestHelper(typeof(decimal)));
        Assert.Equal(-1, GetValueRankTestHelper(typeof(bool)));
        Assert.Equal(-1, GetValueRankTestHelper(typeof(DateTime)));
    }

    [Fact]
    public void GetValueRank_OneDimensionalArrays_ShouldReturnOne()
    {
        // Arrange & Act & Assert
        Assert.Equal(1, GetValueRankTestHelper(typeof(int[])));
        Assert.Equal(1, GetValueRankTestHelper(typeof(string[])));
        Assert.Equal(1, GetValueRankTestHelper(typeof(decimal[])));
        Assert.Equal(1, GetValueRankTestHelper(typeof(bool[])));
    }

    [Fact]
    public void GetValueRank_MultiDimensionalArrays_ShouldReturnCorrectRank()
    {
        // Arrange & Act & Assert
        Assert.Equal(2, GetValueRankTestHelper(typeof(int[,])));
        Assert.Equal(3, GetValueRankTestHelper(typeof(string[,,])));
        Assert.Equal(2, GetValueRankTestHelper(typeof(decimal[,])));
    }

    [Fact]
    public void GetValueRank_JaggedArrays_ShouldReturnOne()
    {
        // Arrange & Act & Assert
        Assert.Equal(1, GetValueRankTestHelper(typeof(int[][])));
        Assert.Equal(1, GetValueRankTestHelper(typeof(string[][])));
        Assert.Equal(1, GetValueRankTestHelper(typeof(decimal[][])));
    }

    [Fact]
    public void GetValueRank_GenericCollections_ShouldReturnOne()
    {
        // Arrange & Act & Assert
        Assert.Equal(1, GetValueRankTestHelper(typeof(List<int>)));
        Assert.Equal(1, GetValueRankTestHelper(typeof(IList<string>)));
        Assert.Equal(1, GetValueRankTestHelper(typeof(IEnumerable<decimal>)));
        Assert.Equal(1, GetValueRankTestHelper(typeof(ICollection<bool>)));
    }

    [Fact]
    public void GetValueRank_NonGenericCollections_ShouldReturnOne()
    {
        // Arrange & Act & Assert
        Assert.Equal(1, GetValueRankTestHelper(typeof(System.Collections.ArrayList)));
        Assert.Equal(1, GetValueRankTestHelper(typeof(System.Collections.Queue)));
        Assert.Equal(1, GetValueRankTestHelper(typeof(System.Collections.Stack)));
    }

    [Fact]
    public void GetValueRank_String_ShouldReturnMinusOne()
    {
        // String implements IEnumerable but should be treated as scalar
        // Arrange & Act & Assert
        Assert.Equal(-1, GetValueRankTestHelper(typeof(string)));
    }

    [Fact]
    public void GetValueRank_SpecialCases_ShouldReturnCorrectValues()
    {
        // Arrange & Act & Assert
        Assert.Equal(1, GetValueRankTestHelper(typeof(byte[])));  // Byte array
        Assert.Equal(1, GetValueRankTestHelper(typeof(char[])));  // Char array
        Assert.Equal(-1, GetValueRankTestHelper(typeof(object))); // Object (scalar)
        Assert.Equal(1, GetValueRankTestHelper(typeof(object[]))); // Object array
    }

    // Helper method to test the private GetValueRank method using reflection
    private static int GetValueRankTestHelper(Type type)
    {
        var method = typeof(CustomNodeManager).GetMethod("GetValueRank",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method); // Ensure the method exists

        return (int)method.Invoke(null, [type])!;
    }
}