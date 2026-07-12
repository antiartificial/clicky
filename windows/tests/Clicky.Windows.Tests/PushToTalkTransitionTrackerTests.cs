using Clicky.Windows.Input;

namespace Clicky.Windows.Tests;

[TestClass]
public sealed class PushToTalkTransitionTrackerTests
{
    [TestMethod]
    public void DefaultKey_IsF13()
    {
        var tracker = new PushToTalkTransitionTracker();

        Assert.AreEqual(PushToTalkKey.F13, tracker.SelectedKey);
    }

    [TestMethod]
    public void RepeatedKeyEvents_EmitExactlyOnePressAndOneRelease()
    {
        var tracker = new PushToTalkTransitionTracker(PushToTalkKey.F18);

        var transitions = new[]
        {
            tracker.ProcessKeyEvent((int)PushToTalkKey.F18, isKeyDown: true),
            tracker.ProcessKeyEvent((int)PushToTalkKey.F18, isKeyDown: true),
            tracker.ProcessKeyEvent((int)PushToTalkKey.F18, isKeyDown: false),
            tracker.ProcessKeyEvent((int)PushToTalkKey.F18, isKeyDown: false),
        };

        CollectionAssert.AreEqual(
            new PushToTalkTransition?[]
            {
                new(PushToTalkKey.F18, PushToTalkTransitionKind.Pressed),
                null,
                new(PushToTalkKey.F18, PushToTalkTransitionKind.Released),
                null,
            },
            transitions);
    }

    [TestMethod]
    public void UnselectedKeyEvents_AreIgnored()
    {
        var tracker = new PushToTalkTransitionTracker(PushToTalkKey.F13);

        var press = tracker.ProcessKeyEvent((int)PushToTalkKey.F14, isKeyDown: true);
        var release = tracker.ProcessKeyEvent((int)PushToTalkKey.F14, isKeyDown: false);

        Assert.IsNull(press);
        Assert.IsNull(release);
    }

    [TestMethod]
    public void ChangingSelectedKeyWhilePressed_ReleasesOldKeyBeforeAcceptingNewKey()
    {
        var tracker = new PushToTalkTransitionTracker(PushToTalkKey.F13);
        var initialPress = tracker.ProcessKeyEvent((int)PushToTalkKey.F13, isKeyDown: true);

        var keyChange = tracker.ChangeSelectedKey(PushToTalkKey.F24);
        var staleRelease = tracker.ProcessKeyEvent((int)PushToTalkKey.F13, isKeyDown: false);
        var newPress = tracker.ProcessKeyEvent((int)PushToTalkKey.F24, isKeyDown: true);

        Assert.AreEqual(
            new PushToTalkTransition(PushToTalkKey.F13, PushToTalkTransitionKind.Pressed),
            initialPress);
        Assert.AreEqual(
            new PushToTalkTransition(PushToTalkKey.F13, PushToTalkTransitionKind.Released),
            keyChange);
        Assert.IsNull(staleRelease);
        Assert.AreEqual(
            new PushToTalkTransition(PushToTalkKey.F24, PushToTalkTransitionKind.Pressed),
            newPress);
    }

    [TestMethod]
    public void SelectingCurrentKey_DoesNotCreateTransition()
    {
        var tracker = new PushToTalkTransitionTracker(PushToTalkKey.F20);
        tracker.ProcessKeyEvent((int)PushToTalkKey.F20, isKeyDown: true);

        var transition = tracker.ChangeSelectedKey(PushToTalkKey.F20);

        Assert.IsNull(transition);
        Assert.AreEqual(
            new PushToTalkTransition(PushToTalkKey.F20, PushToTalkTransitionKind.Released),
            tracker.ProcessKeyEvent((int)PushToTalkKey.F20, isKeyDown: false));
    }

    [TestMethod]
    public void Constructor_RejectsKeysOutsideF13ThroughF24()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new PushToTalkTransitionTracker((PushToTalkKey)0x7B));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new PushToTalkTransitionTracker((PushToTalkKey)0x88));
    }

    [TestMethod]
    public void ChangeSelectedKey_RejectsKeysOutsideF13ThroughF24()
    {
        var tracker = new PushToTalkTransitionTracker();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => tracker.ChangeSelectedKey((PushToTalkKey)0x70));
    }
}
