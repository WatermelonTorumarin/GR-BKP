using System.Text;

namespace GRBKP.Core.Modules.Thunderbird;

/// <summary>
/// Modelo minimo do <c>profiles.ini</c> do Thunderbird, suficiente para resolver e reescrever
/// o perfil padrao. Desde o Thunderbird 68, cada instalacao ("Install&lt;ID&gt;") tem seu proprio
/// perfil padrao dedicado — copiar o profiles.ini antigo por cima nao ativa o perfil recuperado
/// na instalacao nova, porque o Install ID antigo nao corresponde ao da instalacao nova. Essa
/// classe existe para corrigir exatamente isso.
/// </summary>
public sealed class ThunderbirdProfilesIni
{
    private readonly List<IniSection> sections;

    private ThunderbirdProfilesIni(List<IniSection> sections) => this.sections = sections;

    public static ThunderbirdProfilesIni Parse(string text)
    {
        var sections = new List<IniSection>();
        IniSection? current = null;
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                current = new IniSection(trimmed[1..^1]);
                sections.Add(current);
                continue;
            }

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex <= 0 || current is null)
            {
                continue;
            }

            current.Set(trimmed[..separatorIndex].Trim(), trimmed[(separatorIndex + 1)..].Trim());
        }

        return new ThunderbirdProfilesIni(sections);
    }

    /// <summary>
    /// Nome da secao "Install&lt;ID&gt;" (a instalacao especifica do Thunderbird), se existir.
    /// </summary>
    public string? FindInstallSectionName() =>
        sections.FirstOrDefault(section =>
            section.Name.StartsWith("Install", StringComparison.Ordinal))?.Name;

    /// <summary>
    /// Resolve o caminho relativo (ex.: "Profiles/xxxx.default-release") do perfil padrao,
    /// preferindo a secao Install (formato atual) e caindo para o Profile com Default=1
    /// (formato legado) quando nao ha secao Install.
    /// </summary>
    public string? ResolveDefaultProfilePath()
    {
        var install = sections.FirstOrDefault(section =>
            section.Name.StartsWith("Install", StringComparison.Ordinal));
        if (install is not null && install.TryGetValue("Default", out var installDefault))
        {
            return installDefault;
        }

        var legacyDefault = sections.FirstOrDefault(section =>
            section.Name.StartsWith("Profile", StringComparison.Ordinal) &&
            section.TryGetValue("Default", out var flag) &&
            flag == "1");
        return legacyDefault is not null && legacyDefault.TryGetValue("Path", out var path)
            ? path
            : null;
    }

    /// <summary>
    /// Retorna um novo documento com a secao Install identificada por <paramref name="installSectionName"/>
    /// substituida (ou criada) apontando <c>Default</c> para <paramref name="defaultProfileRelativePath"/>
    /// e <c>Locked=1</c>. As demais secoes (incluindo as entradas Profile com os dados do perfil) sao
    /// preservadas inalteradas.
    /// </summary>
    public ThunderbirdProfilesIni WithInstallDefault(string installSectionName, string defaultProfileRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installSectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProfileRelativePath);

        // Remove QUALQUER secao Install existente (tipicamente a da instalacao antiga, trazida
        // pelo profiles.ini recuperado) — nao apenas uma com o mesmo nome do ID novo — para nunca
        // deixar duas secoes Install coexistindo, o que faria FindInstallSectionName() resolver a
        // antiga por ordem de aparicao.
        var updatedSections = sections
            .Where(section => !section.Name.StartsWith("Install", StringComparison.Ordinal))
            .ToList();

        var installSection = new IniSection(installSectionName);
        installSection.Set("Default", defaultProfileRelativePath);
        installSection.Set("Locked", "1");
        updatedSections.Add(installSection);

        return new ThunderbirdProfilesIni(updatedSections);
    }

    public string Serialize()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < sections.Count; index++)
        {
            if (index > 0)
            {
                builder.AppendLine();
            }

            var section = sections[index];
            builder.Append('[').Append(section.Name).Append(']').AppendLine();
            foreach (var (key, value) in section.Entries)
            {
                builder.Append(key).Append('=').Append(value).AppendLine();
            }
        }

        return builder.ToString();
    }

    private sealed class IniSection(string name)
    {
        public string Name { get; } = name;

        public List<(string Key, string Value)> Entries { get; } = [];

        public void Set(string key, string value)
        {
            for (var index = 0; index < Entries.Count; index++)
            {
                if (string.Equals(Entries[index].Key, key, StringComparison.Ordinal))
                {
                    Entries[index] = (key, value);
                    return;
                }
            }

            Entries.Add((key, value));
        }

        public bool TryGetValue(string key, out string value)
        {
            foreach (var entry in Entries)
            {
                if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                {
                    value = entry.Value;
                    return true;
                }
            }

            value = string.Empty;
            return false;
        }
    }
}
