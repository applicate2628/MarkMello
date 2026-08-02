using MarkMello.Applicate.Desktop;
using Xunit;

namespace MarkMello.Applicate.Tests;

/// <summary>
/// Session edit-intent admission for the inactive edit-preview prime.
///
/// <para>The surrounding prime gates are locked by source-text assertions in
/// <c>ApplicateMainWindowBridgeTests</c>. This rule deliberately is NOT: it is a
/// pure total predicate, so the compiler holds it and these tests exercise the
/// real decision instead of matching literals in a source file.</para>
/// </summary>
public sealed class ApplicateInactiveEditPrimeAdmissionTests
{
    // The complete truth table: every one of the 2^3 combinations, each pinned to
    // an independently-derived expectation. Exactly one row defers, so dropping or
    // negating any single operand in the rule flips at least one row red.
    [Theory]
    // isEditMode: an edit-mode call is never a prime candidate at all.
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, false)]
    // No document: nothing to prime, so nothing to defer.
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, false)]
    // Reading a document, intent already established: prime as before.
    [InlineData(false, true, true, false)]
    // Reading a document, intent NOT established: the one deferring case.
    [InlineData(false, true, false, true)]
    public void AdmissionRuleDefersOnlyWhenReadingADocumentWithoutEstablishedEditIntent(
        bool isEditMode,
        bool hasDocument,
        bool editIntentEstablished,
        bool expectedDeferral)
    {
        var deferred = ApplicateMainWindow.ShouldDeferInactiveEditPrimeUntilEditIntent(
            isEditMode,
            hasDocument,
            editIntentEstablished);

        Assert.Equal(expectedDeferral, deferred);
    }

    [Fact]
    public void FreshSessionReadingADocumentDefersThePrimeSoNoSecondCopyIsPaidFor()
    {
        Assert.True(ApplicateMainWindow.ShouldDeferInactiveEditPrimeUntilEditIntent(
            isEditMode: false,
            hasDocument: true,
            editIntentEstablished: false));
    }

    [Fact]
    public void EnteringEditModeOncePrimesEverySubsequentReadingModeCall()
    {
        // The regression this rule must not cause: after the first Ctrl+E the
        // prime has to behave exactly as it did before the change.
        Assert.False(ApplicateMainWindow.ShouldDeferInactiveEditPrimeUntilEditIntent(
            isEditMode: false,
            hasDocument: true,
            editIntentEstablished: true));
    }

    [Fact]
    public void EstablishedEditIntentIsTheOnlyInputThatChangesTheReadingModeVerdict()
    {
        // Pins the rule's discriminator: holding the other two operands at the
        // deferring row, flipping edit intent alone must flip the verdict. A rule
        // that ignored editIntentEstablished would return one constant here.
        var withoutIntent = ApplicateMainWindow.ShouldDeferInactiveEditPrimeUntilEditIntent(
            isEditMode: false,
            hasDocument: true,
            editIntentEstablished: false);
        var withIntent = ApplicateMainWindow.ShouldDeferInactiveEditPrimeUntilEditIntent(
            isEditMode: false,
            hasDocument: true,
            editIntentEstablished: true);

        Assert.NotEqual(withoutIntent, withIntent);
    }

    [Fact]
    public void RuleIsTotalAndDefersInExactlyOneOfItsEightInputCombinations()
    {
        // Guards the "total function" contract itself: if a later edit narrows the
        // rule's domain (say by dropping the hasDocument operand), the deferring
        // combination count changes and this fails, independently of the table.
        var bools = new[] { false, true };
        var deferringCombinations = 0;

        foreach (var isEditMode in bools)
        {
            foreach (var hasDocument in bools)
            {
                foreach (var editIntentEstablished in bools)
                {
                    if (ApplicateMainWindow.ShouldDeferInactiveEditPrimeUntilEditIntent(
                            isEditMode,
                            hasDocument,
                            editIntentEstablished))
                    {
                        deferringCombinations++;
                    }
                }
            }
        }

        Assert.Equal(1, deferringCombinations);
    }
}
