import { createTheme } from '@mui/material/styles'

/**
 * One theme with a light and a dark colour scheme. The choice (light, dark or the system's) is kept by MUI
 * in localStorage. System fonts only: the application works without the internet.
 */
export const theme = createTheme({
  cssVariables: { colorSchemeSelector: 'class' },
  colorSchemes: { light: true, dark: true },
  typography: {
    fontFamily: "system-ui, 'Segoe UI', Roboto, 'Helvetica Neue', Arial, sans-serif",
  },
  shape: { borderRadius: 8 },
})
