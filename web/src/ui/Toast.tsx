/** O recado do `useToast`. A caixa tem altura fixa para o resto da tela não pular. */
export function Toast({ message }: { message: string }) {
  return (
    <div className="toast" role="status" aria-live="polite">
      {message}
    </div>
  )
}
