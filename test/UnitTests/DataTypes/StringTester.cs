using Microsoft.VisualStudio.TestTools.UnitTesting;

using Unknown6656.AutoIt3.Runtime;
using Unknown6656.Testing;

namespace Unknown6656.AutoIt3.Testing.DataTypes;


[TestClass]
internal class StringTester
    : UnitTestRunner
{
    [TestMethod]
    public void Test_01__Conversion()
    {
        const string original = "foo bar";

        Variant variant = original;

        Assert.AreEqual(VariantType.String, variant.Type);

        string s1 = variant.ToString();
        string s2 = (string)variant;

        Assert.AreEqual(original, s1);
        Assert.AreEqual(original, s2);
    }

    [TestMethod]
    public void Test_02__Length()
    {
        foreach (string original in new[] { "foo bar", "", "\0", "\xffff" })
        {
            Variant variant = original;

            Assert.AreEqual(original.Length, variant.Length);
        }
    }

    [TestMethod]
    public void Test_03__Concat()
    {
        const string original1 = "Hello, ";
        const string original2 = "World!";
        Variant variant1 = original1;
        Variant variant2 = original2;

        Variant concat = variant1 & variant2;

        Assert.AreEqual(VariantType.String, concat.Type);
        Assert.AreEqual(original1 + original2, concat.ToString());
    }

    // TODO : add test cases
}
