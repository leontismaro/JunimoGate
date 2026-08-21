namespace JunimoGate.App;

internal sealed class ModOperationBusyState
{
    public bool GeneralOperationActive { get; private set; }
    public bool TranslationOperationActive { get; private set; }
    public bool IsBusy => GeneralOperationActive || TranslationOperationActive;

    public void SetGeneralOperation(bool active) => GeneralOperationActive = active;

    public void SetTranslationOperation(bool active) => TranslationOperationActive = active;
}
