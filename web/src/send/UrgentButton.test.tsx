import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { UrgentButton } from './UrgentButton'

let vibrate: ReturnType<typeof vi.fn>

beforeEach(() => {
  vi.useFakeTimers()
  vibrate = vi.fn()
  Object.defineProperty(navigator, 'vibrate', { value: vibrate, configurable: true, writable: true })
})
afterEach(() => {
  vi.useRealTimers()
  Reflect.deleteProperty(navigator, 'vibrate')
})

function setup(enabled = true) {
  const onUrgent = vi.fn()
  const onShortTap = vi.fn()
  render(<UrgentButton enabled={enabled} onUrgent={onUrgent} onShortTap={onShortTap} />)
  return { onUrgent, onShortTap, button: screen.getByRole('button', { name: 'Segure 1 s para urgente' }) }
}

const advance = (ms: number) =>
  act(() => {
    vi.advanceTimersByTime(ms)
  })

describe('UrgentButton', () => {
  it('solto antes de 1 s: toque curto, nada enviado', () => {
    const { button, onUrgent, onShortTap } = setup()
    fireEvent.pointerDown(button)
    advance(999)
    fireEvent.pointerUp(button)
    expect(onUrgent).not.toHaveBeenCalled()
    expect(onShortTap).toHaveBeenCalledTimes(1)
    advance(5000)
    expect(onUrgent).not.toHaveBeenCalled()
  })

  it('segurado 1 s: vibra 60 ms e envia uma vez só (soltar depois não é toque curto)', () => {
    const { button, onUrgent, onShortTap } = setup()
    fireEvent.pointerDown(button)
    expect(button).toHaveClass('holding')
    advance(1000)
    expect(vibrate).toHaveBeenCalledWith(60)
    expect(onUrgent).toHaveBeenCalledTimes(1)
    expect(button).not.toHaveClass('holding')
    fireEvent.pointerUp(button)
    advance(2000)
    expect(onUrgent).toHaveBeenCalledTimes(1)
    expect(onShortTap).not.toHaveBeenCalled()
  })

  it('o anel de progresso existe (indicador visível do segurar)', () => {
    const { button } = setup()
    expect(button.querySelector('.ring-bar')).not.toBeNull()
  })

  it('sair do botão ou cancelar o toque desiste, sem aviso de toque curto', () => {
    const { button, onUrgent, onShortTap } = setup()
    fireEvent.pointerDown(button)
    advance(500)
    fireEvent.pointerLeave(button)
    advance(2000)
    fireEvent.pointerDown(button)
    advance(500)
    fireEvent.pointerCancel(button)
    advance(2000)
    expect(onUrgent).not.toHaveBeenCalled()
    expect(onShortTap).not.toHaveBeenCalled()
    expect(button).not.toHaveClass('holding')
  })

  it('desabilitado: não faz nada e marca aria-disabled', () => {
    const { button, onUrgent, onShortTap } = setup(false)
    expect(button).toHaveAttribute('aria-disabled', 'true')
    fireEvent.pointerDown(button)
    advance(1500)
    fireEvent.pointerUp(button)
    expect(onUrgent).not.toHaveBeenCalled()
    expect(onShortTap).not.toHaveBeenCalled()
  })

  it('ficar desabilitado no meio do segurar desiste', () => {
    const onUrgent = vi.fn()
    const { rerender } = render(<UrgentButton enabled onUrgent={onUrgent} onShortTap={() => {}} />)
    const button = screen.getByRole('button')
    fireEvent.pointerDown(button)
    advance(500)
    rerender(<UrgentButton enabled={false} onUrgent={onUrgent} onShortTap={() => {}} />)
    advance(1000)
    expect(onUrgent).not.toHaveBeenCalled()
  })

  it('segurar Enter ou Espaço por 1 s também envia; repetição de tecla não reinicia', () => {
    const { button, onUrgent } = setup()
    fireEvent.keyDown(button, { key: 'Enter' })
    advance(600)
    fireEvent.keyDown(button, { key: 'Enter', repeat: true })
    advance(400)
    expect(onUrgent).toHaveBeenCalledTimes(1)
    fireEvent.keyUp(button, { key: 'Enter' })

    fireEvent.keyDown(button, { key: ' ' })
    advance(1000)
    expect(onUrgent).toHaveBeenCalledTimes(2)
  })

  it('tecla solta cedo é toque curto', () => {
    const { button, onUrgent, onShortTap } = setup()
    fireEvent.keyDown(button, { key: ' ' })
    advance(200)
    fireEvent.keyUp(button, { key: ' ' })
    expect(onShortTap).toHaveBeenCalledTimes(1)
    expect(onUrgent).not.toHaveBeenCalled()
  })

  it('sem navigator.vibrate ainda envia', () => {
    Reflect.deleteProperty(navigator, 'vibrate')
    const { button, onUrgent } = setup()
    fireEvent.pointerDown(button)
    advance(1000)
    expect(onUrgent).toHaveBeenCalledTimes(1)
  })
})
