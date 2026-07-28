// src/components/einui/Icon.tsx

export type ProviderIcon = 'codex' | 'claude' | 'local' | 'loading';

interface IconProps {
  provider: ProviderIcon;
  size?: number;          // default 24px
  className?: string;
}

const ICON_MAP: Record<ProviderIcon, { src: string; alt: string }> = {
  codex:   { src: `${import.meta.env.BASE_URL}ai-icon/chatgpt-icon.png`, alt: 'ChatGPT icon' },
  claude:  { src: `${import.meta.env.BASE_URL}ai-icon/claude-ai-icon.png`, alt: 'Claude icon' },
  local:   { src: `${import.meta.env.BASE_URL}ai-icon/ollama-icon.png`, alt: 'Ollama icon' },
  loading: { src: '', alt: '' },
};

export const Icon = ({ provider, size = 24, className = '' }: IconProps) => {
  if (provider === 'loading') {
    return <span className={`icon-loading ${className}`} aria-hidden="true" />;
  }

  const { src, alt } = ICON_MAP[provider];
  return <img src={src} width={size} height={size} className={`icon ${className}`} alt={alt} />;
};
