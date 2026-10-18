namespace Scarab.Services;

public static class DependencyPlanner
{
    public static IReadOnlyList<ModItem> Order(ModItem root, IEnumerable<ModItem> catalog)
    {
        var byName = catalog.ToDictionary(x => x.Name, StringComparer.Ordinal);
        byName[root.Name] = root;
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var chain = new List<string>();
        var ordered = new List<ModItem>();
        void Visit(ModItem mod)
        {
            if (done.Contains(mod.Name)) return;
            if (!visiting.Add(mod.Name)) throw new InvalidDataException("Цикл зависимостей: " + string.Join(" → ", chain.Append(mod.Name)));
            chain.Add(mod.Name);
            foreach (string dependency in mod.Dependencies)
            {
                if (!byName.TryGetValue(dependency, out var item))
                    throw new InvalidDataException($"Мод «{mod.Name}» требует «{dependency}», которой нет в каталоге. Обновите каталог модов.");
                Visit(item);
            }
            chain.RemoveAt(chain.Count - 1);
            visiting.Remove(mod.Name);
            done.Add(mod.Name);
            ordered.Add(mod);
        }
        Visit(root);
        return ordered;
    }
}
