using GRBKP.Core.Adapters;

namespace GRBKP.Windows;

/// <summary>Implementação real de produção de <see cref="IClock"/> — os testes usam fakes fixos.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
