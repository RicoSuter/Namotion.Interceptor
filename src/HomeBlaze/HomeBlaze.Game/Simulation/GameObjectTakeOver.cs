using HomeBlaze.Game.Abstractions;

namespace HomeBlaze.Game.Simulation;

/// <summary>
/// Takes over the object set of a world as outside writes and starts leave it, without overlap checks. Used only on the
/// simulation thread.
/// </summary>
internal static class GameObjectTakeOver
{
    /// <summary>
    /// Takes over the regions and their objects as they are: keeps the entities of subjects that stay, reads every subject
    /// again and indexes it. A subject listed under several names is taken once, and an object whose name an earlier
    /// region listed already gets a new one. The next publish writes the region set, and every region whose objects
    /// differ from the regions of their positions. The objects that enter the world get OnAddedToWorld after that publish,
    /// like placed ones, since a publish that ran between the write and the take-over may have detached them.
    /// </summary>
    /// <returns>The subjects that left the world.</returns>
    public static List<GameObject> TakeOverRegions(this GameWorldState world, Dictionary<string, GameRegion> regions)
    {
        var removed = new List<GameObject>();
        world.Regions.Clear();
        var listings = new List<(GameRegionState Region, Dictionary<string, GameObject> Objects)>(regions.Count);
        var count = CountObjects(regions);
        var next = new Dictionary<string, GameEntity>(count);
        var names = new Dictionary<GameObject, string>(count, ReferenceEqualityComparer.Instance);
        var taken = new TakenObjects(next, names);
        foreach (var (key, subject) in regions)
        {
            if (subject is null || world.Regions.AddPublished(key, subject) is not { } region)
            {
                continue;
            }

            var objects = subject.Objects;
            listings.Add((region, objects));
            foreach (var (name, gameObject) in objects)
            {
                TakeObject(world, name, gameObject, taken);
            }
        }

        foreach (var entity in world.Objects.Values)
        {
            if (!names.ContainsKey(entity.Subject))
            {
                world.Index.Remove(entity);
                world.EntitiesBySubject.Remove(entity.Subject);
                world.ForgetUnpublishedObject(entity.Subject);
                entity.Region = null;
                removed.Add(entity.Subject);
            }
        }

        world.Objects.Clear();
        world.NamesBySubject.Clear();
        foreach (var (name, entity) in next)
        {
            if (!world.EntitiesBySubject.ContainsKey(entity.Subject))
            {
                world.AddUnpublishedObject(entity.Subject);
            }

            world.Objects[name] = entity;
            world.NamesBySubject[entity.Subject] = name;
            world.EntitiesBySubject[entity.Subject] = entity;
            entity.ReadFromSubject();
            world.Index.AddOrUpdate(entity);
            world.Regions.Assign(name, entity);
        }

        foreach (var (region, objects) in listings)
        {
            if (!region.IsStray && (region.Objects.Count == 0 || !IsListedAsIs(region, objects)))
            {
                world.Regions.MarkChanged(region);
                AddLeavingObjects(world, region, objects);
            }
        }

        world.Regions.RequestRegionSetWrite();
        return removed;
    }

    /// <summary>
    /// Takes over the objects of one region as they are: objects of the region that the set leaves out leave the world,
    /// new ones enter it, under a new name when another object has theirs, and kept ones are read again. The next publish
    /// writes the region, after which the new objects get OnAddedToWorld. A region that is not part of the world only adds
    /// its new objects.
    /// </summary>
    /// <returns>The subjects that left the world.</returns>
    public static List<GameObject> TakeOverRegion(this GameWorldState world, GameRegion subject, Dictionary<string, GameObject> objects)
    {
        var removed = new List<GameObject>();
        var region = world.Regions.Find(subject);
        if (region is { IsStray: false })
        {
            RemoveObjectsLeftOut(world, region, objects, removed);
        }

        foreach (var (name, gameObject) in objects)
        {
            if (gameObject is null or GamePlayer)
            {
                continue;
            }

            if (world.NamesBySubject.ContainsKey(gameObject))
            {
                world.RefreshSubject(gameObject);
                continue;
            }

            var entity = new GameEntity(gameObject);
            entity.ReadFromSubject();
            world.AddEntity(entity, world.Objects.ContainsKey(name) ? GameWorldState.CreateObjectName(gameObject, world.Objects) : name);
            world.AddUnpublishedObject(gameObject);
        }

        if (region is { IsStray: false })
        {
            world.Regions.MarkChanged(region);
            AddLeavingObjects(world, region, objects);
        }

        return removed;
    }

    private static int CountObjects(Dictionary<string, GameRegion> regions)
    {
        var count = 0;
        foreach (var subject in regions.Values)
        {
            count += subject?.Objects.Count ?? 0;
        }

        return count;
    }

    // A player subject belongs to Players, and a subject under a second name is the same object.
    private static void TakeObject(GameWorldState world, string name, GameObject? gameObject, TakenObjects taken)
    {
        if (gameObject is null or GamePlayer || taken.Names.ContainsKey(gameObject))
        {
            return;
        }

        if (!world.NamesBySubject.ContainsKey(gameObject) || !world.EntitiesBySubject.TryGetValue(gameObject, out var entity))
        {
            entity = new GameEntity(gameObject);
        }

        var entityName = taken.Entities.ContainsKey(name) ? GameWorldState.CreateObjectName(gameObject, taken.Entities) : name;
        taken.Entities[entityName] = entity;
        taken.Names[gameObject] = entityName;
    }

    // Whether the region's subject lists exactly the region's objects under their names.
    private static bool IsListedAsIs(GameRegionState region, Dictionary<string, GameObject> objects)
    {
        if (region.Objects.Count != objects.Count)
        {
            return false;
        }

        foreach (var (name, entity) in region.Objects)
        {
            if (!objects.TryGetValue(name, out var listed) || !ReferenceEquals(listed, entity.Subject))
            {
                return false;
            }
        }

        return true;
    }

    // The objects of the world that the region's subject lists although they are in another region, which a write of the
    // region keeps until the write of their region holds them, so they never leave the graph in between.
    private static void AddLeavingObjects(GameWorldState world, GameRegionState region, Dictionary<string, GameObject> objects)
    {
        foreach (var gameObject in objects.Values)
        {
            if (gameObject is not null && world.NamesBySubject.TryGetValue(gameObject, out var name)
                && world.EntitiesBySubject.TryGetValue(gameObject, out var entity) && region.IsLeaving(entity))
            {
                GameRegionMap.AddLeaving(region, name, entity);
            }
        }
    }

    private static void RemoveObjectsLeftOut(GameWorldState world, GameRegionState region, Dictionary<string, GameObject> objects,
        List<GameObject> removed)
    {
        var listed = new HashSet<GameObject>(objects.Count, ReferenceEqualityComparer.Instance);
        foreach (var gameObject in objects.Values)
        {
            if (gameObject is not null)
            {
                listed.Add(gameObject);
            }
        }

        foreach (var entity in region.Objects.Values)
        {
            if (!listed.Contains(entity.Subject))
            {
                removed.Add(entity.Subject);
            }
        }

        foreach (var gameObject in removed)
        {
            world.RemoveObject(gameObject);
        }
    }

    // The objects a take-over of the whole set has taken so far: the entities by their new names and the names by subject.
    private readonly record struct TakenObjects(Dictionary<string, GameEntity> Entities, Dictionary<GameObject, string> Names);
}
