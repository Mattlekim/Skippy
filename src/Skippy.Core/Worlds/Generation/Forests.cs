namespace Skippy.Core.Worlds.Generation;

/// <summary>
/// What kind of wood grows where, and what that wood is made of.
///
/// Biome says whether there are trees at all; this says which ones. Every wooded tile
/// carries a <see cref="ForestKind"/> taken from a low-frequency stand field, so the
/// map grows recognisable, separate woods — an evergreen forest here, a broadleaf one
/// over the ridge, a belt of mixed ground between them — instead of one tree per biome
/// stamped from horizon to horizon.
///
/// The one climate coupling is that cold favours evergreens: the same stand field reads
/// as pine up on a cold plateau and as oak down in the warm. That is what puts the
/// evergreen woods where a player expects to find them without pinning them to the
/// taiga — a temperate forest can perfectly well be a pine wood.
/// </summary>
public static class Forests
{
    /// <summary>
    /// The temperature an evergreen and a broadleaf wood are equally likely at. Below it
    /// the stand field is pushed towards evergreen, above it towards broadleaf. It sits in
    /// the middle of the temperate band, so that band holds the whole range of woods and
    /// the taiga below it comes out overwhelmingly (but not entirely) evergreen.
    /// </summary>
    private const float ConiferPivot = 0.40f;

    /// <summary>
    /// How hard temperature tilts the stand field. Strong enough that the cold end of the
    /// map is evergreen country and the warm end has hardly a conifer in it, while leaving
    /// the field itself in charge of which wood is which anywhere in between.
    /// </summary>
    private const float ConiferCold = 1.5f;

    /// <summary>
    /// Where the tilted field is cut into the four kinds along the evergreen↔broadleaf axis.
    /// The two mixed bands sit either side of the middle and are deliberately narrower than
    /// the pure ones: a mixed wood is the ground where two forests meet, and should read as
    /// a transition rather than as the commonest thing on the map.
    /// </summary>
    private const float ConiferAbove = 0.66f;
    private const float ConiferMixedAbove = 0.50f;
    private const float BroadleafMixedAbove = 0.34f;

    /// <summary>Above this, a jungle grows broadleaf up through the palms in real numbers.</summary>
    private const float PalmMixedAbove = 0.70f;

    /// <summary>
    /// The wood a tile stands in, from its biome, its temperature, and the stand field
    /// (0..1, spread even) that decides which wood is where.
    /// </summary>
    public static ForestKind Classify(Biome biome, float temperature, float stand)
    {
        // The tropics are the palms' own — nothing else takes the canopy off them — so the
        // only question a jungle asks is how much broadleaf comes up underneath.
        if (biome == Biome.Jungle)
            return stand > PalmMixedAbove ? ForestKind.PalmMixed : ForestKind.Palm;

        float conifer = stand + ConiferCold * (ConiferPivot - temperature);
        return conifer > ConiferAbove ? ForestKind.Conifer
             : conifer > ConiferMixedAbove ? ForestKind.ConiferMixed
             : conifer > BroadleafMixedAbove ? ForestKind.BroadleafMixed
             : ForestKind.Broadleaf;
    }

    /// <summary>
    /// What a wood of this kind is planted with: the tree that has the canopy, the one
    /// growing through it, and the primary's share of the trees on the ground.
    ///
    /// A pure stand keeps a tenth or so for the other kind rather than none at all —
    /// a wood with literally one species in it reads as wallpaper, and the odd stranger
    /// among the ranks is what makes the dominance look chosen instead of enforced.
    /// </summary>
    public static void Mix(ForestKind kind, out PlantKind primary, out PlantKind secondary, out float primaryShare)
    {
        (primary, secondary, primaryShare) = kind switch
        {
            ForestKind.Conifer        => (PlantKind.PineTree, PlantKind.BroadleafTree, 0.88f),
            ForestKind.ConiferMixed   => (PlantKind.PineTree, PlantKind.BroadleafTree, 0.62f),
            ForestKind.BroadleafMixed => (PlantKind.BroadleafTree, PlantKind.PineTree, 0.62f),
            ForestKind.Palm           => (PlantKind.PalmTree, PlantKind.BroadleafTree, 0.85f),
            ForestKind.PalmMixed      => (PlantKind.PalmTree, PlantKind.BroadleafTree, 0.55f),
            _                         => (PlantKind.BroadleafTree, PlantKind.PineTree, 0.88f),
        };
    }

    /// <summary>The tree that owns a wood of this kind — the one a passer-by would name it after.</summary>
    public static PlantKind Primary(ForestKind kind)
    {
        Mix(kind, out PlantKind primary, out _, out _);
        return primary;
    }

    /// <summary>
    /// Whether a biome grows trees at all, and so whether its <see cref="ForestKind"/>
    /// means anything. Open country counts: a grassland's scattered trees are drawn from
    /// the same stand as the wood next door, so a cold plain is dotted with pines.
    /// </summary>
    public static bool Wooded(Biome biome)
        => biome is Biome.TemperateForest or Biome.Taiga or Biome.Jungle
                 or Biome.Grassland or Biome.Savanna or Biome.Swamp;
}
