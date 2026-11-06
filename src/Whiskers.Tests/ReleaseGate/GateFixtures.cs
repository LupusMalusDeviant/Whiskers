namespace Whiskers.Tests.ReleaseGate;

/// <summary>
/// Test classes for the gate's own tests (case counts, static skips). They live in a non-public class, so the runner's
/// discovery does not see them and they never run; the gate's <see cref="TestCatalog"/> discovers them on request.
/// </summary>
internal static class GateFixtures
{
    public class CaseCountFixture
    {
        [Fact]
        public void A_fact() { }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void Two_inline_rows(int value) => Assert.True(value > 0);

        public static TheoryData<int> Three => new() { 1, 2, 3 };

        [Theory]
        [MemberData(nameof(Three))]
        public void Three_member_rows(int value) => Assert.True(value > 0);

        // Not serializable: xUnit 2 reports the whole theory as one case.
        public static IEnumerable<object[]> Opaque => [[new object()], [new object()]];

        [Theory]
        [MemberData(nameof(Opaque))]
        public void Opaque_rows(object value) => Assert.NotNull(value);
    }

    public class SkipFixture
    {
        [Fact(Skip = "fixture: a statically skipped test")]
        public void Skipped_fact() { }

        [Theory]
        [InlineData(1)]
        [InlineData(2, Skip = "fixture: a statically skipped data row")]
        public void One_row_skipped(int value) => Assert.True(value > 0);
    }
}
