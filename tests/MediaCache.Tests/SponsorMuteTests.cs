// Copyright (c) Bili Copilot. All rights reserved.

using BiliCopilot.UI.Services.Sponsor;

namespace MediaCache.Tests;

/// <summary>
/// 换集之后静音状态必须复位 —— 这是实测过的缺陷：
/// 上一集停在 mute 段时，下一集会带着「已静音」标记进来，音量再也不压、也不恢复，整集没声音.
/// 这里断言复位函数的契约.
/// </summary>
internal static class SponsorMuteTests
{
    public static void Register()
    {
        TestHost.Add("静音：进入 mute 段记住原音量，重复进入不覆盖", () =>
        {
            var state = new SponsorMuteState();

            Assert.True(state.Mute(60), "第一次应真的进入静音");
            Assert.True(state.IsMuted, "标记为已静音");
            Assert.Equal(60d, state.BackupVolume, "原音量");

            Assert.False(state.Mute(80), "重复 tick 不该再压一次");
            Assert.Equal(60d, state.BackupVolume, "原音量不应被覆盖");
        });

        TestHost.Add("静音：离开 mute 段恢复原音量，再恢复一次是空操作", () =>
        {
            var state = new SponsorMuteState();
            state.Mute(45);

            Assert.True(state.TryRestore(out var volume), "应真的恢复");
            Assert.Equal(45d, volume, "恢复音量");
            Assert.False(state.IsMuted, "标记已清");

            Assert.False(state.TryRestore(out _), "第二次不该再改音量");
        });

        TestHost.Add("换集：停在 mute 段后复位，下一集音量收回来且能重新静音（缺陷回归）", () =>
        {
            var state = new SponsorMuteState();

            // 第 1 P：在 mute 段里被压到 0，原音量 60.
            state.Mute(60);
            Assert.True(state.IsMuted, "第 1 P 结束时仍是静音状态");

            // 换集：InitializeCoreAsync 的收尾必须拿到「要还给播放器的音量」，并清掉标记.
            var restore = state.ResetForNewMedia();
            Assert.NotNull(restore, "应该给出要恢复的音量");
            Assert.Equal(60d, restore!.Value, "还给播放器的音量");
            Assert.False(state.IsMuted, "换集后必须回到未静音");

            // 第 2 P：再次进入 mute 段时必须真的压音量（旧实现这里直接 return，整集没声音）.
            Assert.True(state.Mute(75), "换集后应能重新进入静音");
            Assert.Equal(75d, state.BackupVolume, "新一段的原音量");
        });

        TestHost.Add("换集：本来没静音时复位不产生任何音量动作", () =>
        {
            var state = new SponsorMuteState();

            Assert.Equal(null, state.ResetForNewMedia(), "不该有要恢复的音量");
            Assert.False(state.IsMuted, "仍是未静音");
            Assert.Equal(SponsorMuteState.DefaultVolume, state.BackupVolume, "原音量回到默认值");
        });

        TestHost.Add("静音：坏音量（NaN / 负数）按默认值记，不会把坏值写回播放器", () =>
        {
            var nan = new SponsorMuteState();
            Assert.True(nan.Mute(double.NaN), "NaN 音量也应进入静音");
            Assert.Equal(SponsorMuteState.DefaultVolume, nan.BackupVolume, "NaN → 默认值");

            var negative = new SponsorMuteState();
            Assert.True(negative.Mute(-3), "负音量也应进入静音");
            Assert.Equal(SponsorMuteState.DefaultVolume, negative.BackupVolume, "负数 → 默认值");
        });

        TestHost.Add("静音：两次换集之间状态不会互相污染", () =>
        {
            var state = new SponsorMuteState();

            state.Mute(30);
            var first = state.ResetForNewMedia();
            state.Mute(90);
            var second = state.ResetForNewMedia();

            Assert.Equal(30d, first!.Value, "第一次恢复的音量");
            Assert.Equal(90d, second!.Value, "第二次恢复的音量");
            Assert.False(state.IsMuted, "最后是未静音");
        });
    }
}
