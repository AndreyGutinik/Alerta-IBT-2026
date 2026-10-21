import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MicButton } from './MicButton'
import { getSpeechRecognition, type SpeechRecognitionEventLike } from './useSpeech'

class FakeRecognition {
  static instances: FakeRecognition[] = []
  lang = ''
  continuous = true
  interimResults = true
  maxAlternatives = 0
  started = false
  aborted = false
  onresult: ((e: SpeechRecognitionEventLike) => void) | null = null
  onerror: ((e: { error?: string }) => void) | null = null
  onend: (() => void) | null = null
  constructor() {
    FakeRecognition.instances.push(this)
  }
  start() {
    this.started = true
  }
  stop() {
    this.onend?.()
  }
  abort() {
    this.aborted = true
  }
  say(text: string) {
    this.onresult?.({ results: [{ 0: { transcript: text }, length: 1 }] })
    this.onend?.()
  }
}

function install(name: 'SpeechRecognition' | 'webkitSpeechRecognition') {
  FakeRecognition.instances = []
  Object.defineProperty(window, name, { value: FakeRecognition, configurable: true, writable: true })
}

afterEach(() => {
  Reflect.deleteProperty(window, 'SpeechRecognition')
  Reflect.deleteProperty(window, 'webkitSpeechRecognition')
})

describe('MicButton', () => {
  it('sem SpeechRecognition no navegador: sem botão', () => {
    render(<MicButton onText={() => {}} onMessage={() => {}} />)
    expect(screen.queryByRole('button', { name: 'Ditar' })).not.toBeInTheDocument()
    expect(getSpeechRecognition()).toBeNull()
  })

  it('funciona com o prefixo webkit', () => {
    install('webkitSpeechRecognition')
    expect(getSpeechRecognition()).toBe(FakeRecognition)
    render(<MicButton onText={() => {}} onMessage={() => {}} />)
    expect(screen.getByRole('button', { name: 'Ditar' })).toBeInTheDocument()
  })

  it('clicar começa a ouvir em pt-BR, marca rec e avisa; o resultado vai para onText; onend tira o rec', async () => {
    install('SpeechRecognition')
    const onText = vi.fn()
    const onMessage = vi.fn()
    render(<MicButton onText={onText} onMessage={onMessage} />)
    const button = screen.getByRole('button', { name: 'Ditar' })
    await userEvent.click(button)

    const rec = FakeRecognition.instances[0]
    expect(rec.lang).toBe('pt-BR')
    expect(rec.started).toBe(true)
    expect(rec.continuous).toBe(false)
    expect(button).toHaveClass('rec')
    expect(onMessage).toHaveBeenCalledWith('Ouvindo…')

    act(() => rec.say('corolla preto qwe2f45'))
    expect(onText).toHaveBeenCalledWith('corolla preto qwe2f45')
    expect(button).not.toHaveClass('rec')
  })

  it('segundo toque enquanto ouve para', async () => {
    install('SpeechRecognition')
    render(<MicButton onText={() => {}} onMessage={() => {}} />)
    const button = screen.getByRole('button', { name: 'Ditar' })
    await userEvent.click(button)
    await userEvent.click(button)
    expect(button).not.toHaveClass('rec')
    expect(FakeRecognition.instances).toHaveLength(1)
  })

  it('microfone negado explica o que fazer', async () => {
    install('SpeechRecognition')
    const onMessage = vi.fn()
    render(<MicButton onText={() => {}} onMessage={onMessage} />)
    await userEvent.click(screen.getByRole('button', { name: 'Ditar' }))
    act(() => FakeRecognition.instances[0].onerror?.({ error: 'not-allowed' }))
    expect(onMessage).toHaveBeenLastCalledWith('Permita o microfone nas configurações do navegador.')
  })

  it('sair da tela aborta o ditado', async () => {
    install('SpeechRecognition')
    const { unmount } = render(<MicButton onText={() => {}} onMessage={() => {}} />)
    await userEvent.click(screen.getByRole('button', { name: 'Ditar' }))
    unmount()
    expect(FakeRecognition.instances[0].aborted).toBe(true)
  })
})
