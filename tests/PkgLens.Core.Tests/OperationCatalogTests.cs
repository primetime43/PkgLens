using PkgLens.Core.Shared;

namespace PkgLens.Core.Tests;

public sealed class OperationCatalogTests
{
    [Fact]
    public void Catalog_HasUniqueIdsAndCompleteSurfaceMappings()
    {
        Assert.Equal(OperationCatalog.All.Count, OperationCatalog.All.Select(operation => operation.Id).Distinct().Count());

        foreach (PackageRecommendedAction action in Enum.GetValues<PackageRecommendedAction>())
            Assert.Equal(action, OperationCatalog.ForRecommendation(action).RecommendationAction);

        foreach (BatchOperation operation in Enum.GetValues<BatchOperation>())
            Assert.Equal(operation, OperationCatalog.ForBatch(operation).BatchOperation);
    }

    [Fact]
    public void MenuLocations_ReferenceKnownGroupsAndBindableRules()
    {
        var groups = OperationCatalog.MenuGroups.ToDictionary(group => (group.Menu, group.Id));
        foreach ((OperationDefinition definition, OperationMenuLocation location) in
                 Enum.GetValues<OperationMenu>().SelectMany(OperationCatalog.MenuItems))
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.MenuHeader));
            Assert.False(string.IsNullOrWhiteSpace(definition.Tooltip));
            _ = OperationCatalog.MenuBindingPath(definition.MenuEligibility);
            if (location.Group is not null)
                Assert.True(groups.ContainsKey((location.Menu, location.Group)));
        }
    }

    [Fact]
    public void BatchChoices_HaveUserFacingNamesAndDescriptions()
    {
        Assert.Equal(Enum.GetValues<BatchOperation>().Length, OperationCatalog.BatchOperations.Count);
        Assert.All(OperationCatalog.BatchOperations, operation =>
        {
            Assert.False(string.IsNullOrWhiteSpace(operation.BatchLabel));
            Assert.False(string.IsNullOrWhiteSpace(operation.BatchDescription));
            Assert.NotEqual(OperationEligibilityRule.Never, operation.BatchEligibility);
        });
    }
}
