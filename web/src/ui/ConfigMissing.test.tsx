import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { ConfigMissing } from './ConfigMissing'

describe('ConfigMissing', () => {
  it('diz que o site não está configurado e lista o que falta', () => {
    render(<ConfigMissing missing={['VITE_FIREBASE_API_KEY', 'VITE_FIREBASE_APP_ID']} />)
    expect(screen.getByRole('heading', { name: 'Site ainda não configurado' })).toBeInTheDocument()
    expect(screen.getByText('Configuração do Firebase ausente:')).toBeInTheDocument()
    expect(screen.getByText('VITE_FIREBASE_API_KEY')).toBeInTheDocument()
    expect(screen.getByText('VITE_FIREBASE_APP_ID')).toBeInTheDocument()
  })
})
