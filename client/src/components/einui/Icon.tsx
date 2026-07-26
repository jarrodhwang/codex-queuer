// src/components/einui/Icon.tsx

export type ProviderIcon = 'codex' | 'claude' | 'local' | 'loading';

interface IconProps {
  provider: ProviderIcon;
  size?: number;          // default 24px
  className?: string;
}

const ICON_MAP: Record<ProviderIcon, { src: string; alt: string }> = {
  codex:   { src: 'https://cdn-icons-png.flaticon.com/512/1828/1828667.png', alt: 'Codex icon' },
  claude:  { src: 'https://cdn-icons-png.flaticon.com/512/2923/2923676.png',  alt: 'Claude icon' },
  local:   { src: '/icons/local.svg',                                      alt: 'Local icon' }, // keep current
  loading: { src: '', alt: '' }                                            // handled by CSS spinner
};

export const Icon = ({ provider, size = 24, className = '' }: IconProps) => {
  if (provider === 'loading') {
    return <span className={`icon-loading ${className}`} aria-hidden="true" />;
  }

  const { src, alt } = ICON_MAP[provider];
  // For Codex & Claude we use external PNGs; for local we keep the SVG that already lives in public
  return <img src={src} width={size} height={size} className={`icon ${className}`} alt={alt} />;
};
