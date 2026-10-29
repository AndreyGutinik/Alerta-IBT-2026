import { act, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { LONG_PRESS_MS, LongPressTips, TIP_SHOW_MS } from './LongPressTips'

function setup(onClick = vi.fn()) {
  render(
    <>
      <LongPressTips />
      <button type="button" title="Cancelar aviso" onClick={onClick}>
        x
      </button>
      <button type="button">sem dica</button>
    </>,
  )
  return { onClick, button: screen.getByTitle('Cancelar aviso') }
}

const touch = { pointerType: 'touch', clientX: 10, clientY: 10 }

describe('LongPressTips', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => vi.useRealTimers())

  it('mostra o title ao segurar o dedo e some sozinho', () => {
    const { button } = setup()
    fireEvent.pointerDown(button, touch)
    act(() => void vi.advanceTimersByTime(LONG_PRESS_MS + 10))
    expect(screen.getByRole('tooltip')).toHaveTextContent('Cancelar aviso')
    act(() => void vi.advanceTimersByTime(TIP_SHOW_MS + 10))
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
  })

  it('o toque que termina a pressão longa não aciona o botão', () => {
    const { button, onClick } = setup()
    fireEvent.pointerDown(button, touch)
    act(() => void vi.advanceTimersByTime(LONG_PRESS_MS + 10))
    fireEvent.pointerUp(button, touch)
    fireEvent.click(button)
    expect(onClick).not.toHaveBeenCalled()
    // o toque seguinte volta a valer
    fireEvent.pointerDown(button, touch)
    fireEvent.pointerUp(button, touch)
    fireEvent.click(button)
    expect(onClick).toHaveBeenCalledTimes(1)
  })

  it('toque curto não mostra dica e o clique passa', () => {
    const { button, onClick } = setup()
    fireEvent.pointerDown(button, touch)
    act(() => void vi.advanceTimersByTime(100))
    fireEvent.pointerUp(button, touch)
    fireEvent.click(button)
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
    expect(onClick).toHaveBeenCalledTimes(1)
  })

  it('arrastar o dedo cancela a dica', () => {
    const { button } = setup()
    fireEvent.pointerDown(button, touch)
    fireEvent.pointerMove(button, { ...touch, clientY: 60 })
    act(() => void vi.advanceTimersByTime(LONG_PRESS_MS + 10))
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
  })

  it('mouse usa o title nativo e elemento sem title é ignorado', () => {
    const { button } = setup()
    fireEvent.pointerDown(button, { ...touch, pointerType: 'mouse' })
    act(() => void vi.advanceTimersByTime(LONG_PRESS_MS + 10))
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
    fireEvent.pointerDown(screen.getByText('sem dica'), touch)
    act(() => void vi.advanceTimersByTime(LONG_PRESS_MS + 10))
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument()
  })
})
