/**
 * O recado do `useToast` (ou uma nota fixa, com `live={false}`, que não é anunciada a cada mudança, como a
 * contagem regressiva). A caixa tem altura fixa para o resto da tela não pular.
 */
export function Toast({ message, live = true }: { message: string; live?: boolean }) {
  return (
    <div className="toast" role={live ? 'status' : undefined} aria-live={live ? 'polite' : 'off'}>
      {message}
    </div>
  )
}
