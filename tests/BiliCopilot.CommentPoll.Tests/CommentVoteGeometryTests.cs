// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services;
using Xunit;

namespace BiliCopilot.CommentPoll.Tests;

/// <summary>
/// 饼图的角度与点击命中。这块是纯算术，界面上很难看出对错，只能靠测.
/// </summary>
public class CommentVoteGeometryTests
{
    [Fact]
    public void NoVotes_IsSplitEvenly()
    {
        var sweeps = CommentVoteGeometry.ComputeSweeps([0, 0]);

        Assert.Equal(2, sweeps.Count);
        Assert.Equal(180d, sweeps[0], 3);
        Assert.Equal(180d, sweeps[1], 3);
        Assert.Equal(360d, sweeps.Sum(), 3);
    }

    [Fact]
    public void ThreeOptionsWithoutVotes_IsSplitEvenly()
    {
        var sweeps = CommentVoteGeometry.ComputeSweeps([0, 0, 0]);

        Assert.All(sweeps, sweep => Assert.Equal(120d, sweep, 3));
        Assert.Equal(360d, sweeps.Sum(), 3);
    }

    [Fact]
    public void Votes_AreProportional_ButZeroCountKeepsASliver()
    {
        var sweeps = CommentVoteGeometry.ComputeSweeps([4, 0]);

        Assert.Equal(360d, sweeps.Sum(), 3);
        Assert.True(sweeps[0] > sweeps[1], "票多的片必须更大");
        Assert.True(sweeps[1] >= CommentVoteGeometry.MinimumSweep - 0.001, "0 票的片仍要能点到");
    }

    [Fact]
    public void SweepsAlwaysFillTheCircle()
    {
        long[][] cases =
        [
            [0],
            [5],
            [1, 1, 1, 1, 1],
            [7, 2, 0, 0],
            [0, 0, 3],
            [1000, 1],
        ];

        foreach (var counts in cases)
        {
            var sweeps = CommentVoteGeometry.ComputeSweeps(counts);
            Assert.Equal(counts.Length, sweeps.Count);
            Assert.Equal(360d, sweeps.Sum(), 3);
            Assert.All(sweeps, sweep => Assert.True(sweep >= 0));
        }
    }

    [Fact]
    public void SingleOption_TakesTheWholeCircle()
    {
        Assert.Equal([360d], CommentVoteGeometry.ComputeSweeps([9]));
    }

    [Fact]
    public void EmptyInput_IsEmpty()
    {
        Assert.Empty(CommentVoteGeometry.ComputeSweeps([]));
        Assert.Equal(-1, CommentVoteGeometry.HitTest([], 90));
    }

    [Fact]
    public void NegativeCountsAreIgnored()
    {
        var sweeps = CommentVoteGeometry.ComputeSweeps([-5, 3]);

        Assert.Equal(360d, sweeps.Sum(), 3);
        Assert.True(sweeps[1] > sweeps[0]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(45, 0)]
    [InlineData(179.9, 0)]
    [InlineData(180, 1)]
    [InlineData(300, 1)]
    [InlineData(359.9, 1)]
    public void HitTest_OnTwoEqualHalves(double angle, int expected)
        => Assert.Equal(expected, CommentVoteGeometry.HitTest([180, 180], angle));

    [Fact]
    public void HitTest_WrapsAroundTheCircle()
    {
        // -90 就是 270，落在右半（第二片）.
        Assert.Equal(1, CommentVoteGeometry.HitTest([180, 180], -90));
        Assert.Equal(0, CommentVoteGeometry.HitTest([180, 180], 360));
        Assert.Equal(1, CommentVoteGeometry.HitTest([180, 180], 540));
    }

    [Fact]
    public void HitTest_FindsTheThinSlice()
    {
        // 354 / 6：角度 355 只能落在第二片.
        var sweeps = CommentVoteGeometry.ComputeSweeps([100, 0]);
        Assert.True(sweeps[1] >= 5.9 && sweeps[1] <= 6.1);

        var angleOfThinSlice = sweeps[0] + (sweeps[1] / 2);
        Assert.Equal(1, CommentVoteGeometry.HitTest(sweeps, angleOfThinSlice));
        Assert.Equal(0, CommentVoteGeometry.HitTest(sweeps, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(360, 0)]
    [InlineData(-360, 0)]
    [InlineData(370, 10)]
    [InlineData(-10, 350)]
    [InlineData(725, 5)]
    public void Normalize_FoldsIntoOneTurn(double angle, double expected)
        => Assert.Equal(expected, CommentVoteGeometry.Normalize(angle), 3);
}
