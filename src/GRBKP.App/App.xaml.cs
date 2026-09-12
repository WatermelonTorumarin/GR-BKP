using System.Windows;
using System.Windows.Threading;

namespace GRBKP.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
    }

    /// <summary>
    /// No WPF, uma exceção que escapa de um manipulador de evento derruba o processo inteiro — e,
    /// para o técnico, o app simplesmente SOME no meio do trabalho, sem dizer nada e sem deixar
    /// pista do que houve. Num utilitário que roda em máquina de cliente, isso é inaceitável:
    /// aqui o erro é mostrado na tela de resultado e o app continua vivo, pronto para voltar à
    /// tela inicial.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        if (MainWindow is MainWindow janela)
        {
            janela.ShowUnexpectedError(e.Exception);
            return;
        }

        MessageBox.Show(
            $"Ocorreu um erro inesperado:\n\n{e.Exception.Message}",
            "GR BKP",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
