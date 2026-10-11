import DarkModeOutlined from '@mui/icons-material/DarkModeOutlined'
import LightModeOutlined from '@mui/icons-material/LightModeOutlined'
import MenuIcon from '@mui/icons-material/Menu'
import AppBar from '@mui/material/AppBar'
import Box from '@mui/material/Box'
import Chip from '@mui/material/Chip'
import Drawer from '@mui/material/Drawer'
import IconButton from '@mui/material/IconButton'
import List from '@mui/material/List'
import ListItemButton from '@mui/material/ListItemButton'
import ListItemIcon from '@mui/material/ListItemIcon'
import ListItemText from '@mui/material/ListItemText'
import { useColorScheme, useTheme } from '@mui/material/styles'
import Toolbar from '@mui/material/Toolbar'
import Tooltip from '@mui/material/Tooltip'
import Typography from '@mui/material/Typography'
import useMediaQuery from '@mui/material/useMediaQuery'
import { useState, type ReactNode } from 'react'
import { Link, useLocation } from 'react-router'
import type { TradingMode } from '../api/session'
import { navigation } from './navigation'

const drawerWidth = 232

/** The menu item of a screen stays marked on the screen's own sub-pages. */
function isCurrent(path: string, pathname: string): boolean {
  return path === '/' ? pathname === '/' : pathname === path || pathname.startsWith(`${path}/`)
}

/** What the header always shows. `undefined` means "not known yet" and is shown as such, never guessed. */
export interface ShellStatus {
  mode: TradingMode | undefined
  account: string | undefined
  reconciliation: string | undefined
}

interface AppShellProps {
  status: ShellStatus
  children: ReactNode
}

/** The frame of every screen: a header that never scrolls away, the menu of six screens, the content. */
export function AppShell({ status, children }: AppShellProps) {
  const theme = useTheme()
  const wide = useMediaQuery(theme.breakpoints.up('md'), { noSsr: true })
  const { pathname } = useLocation()
  const [menuOpen, setMenuOpen] = useState(false)

  const menu = (
    <>
      <Toolbar />
      <List component="div" sx={{ px: 1 }}>
        {navigation.map(({ path, label, icon: Icon }) => (
          <ListItemButton
            key={path}
            component={Link}
            to={path}
            selected={isCurrent(path, pathname)}
            aria-current={isCurrent(path, pathname) ? 'page' : undefined}
            onClick={() => {
              setMenuOpen(false)
            }}
            sx={{ borderRadius: 1 }}
          >
            <ListItemIcon sx={{ minWidth: 40 }}>
              <Icon />
            </ListItemIcon>
            <ListItemText primary={label} />
          </ListItemButton>
        ))}
      </List>
    </>
  )

  return (
    <Box sx={{ display: 'flex', minHeight: '100vh' }}>
      <AppBar
        position="fixed"
        color="default"
        elevation={0}
        sx={{ borderBottom: 1, borderColor: 'divider', zIndex: theme.zIndex.drawer + 1 }}
      >
        <Toolbar sx={{ gap: 1 }}>
          {!wide && (
            <IconButton
              edge="start"
              aria-label="Open menu"
              onClick={() => {
                setMenuOpen(true)
              }}
            >
              <MenuIcon />
            </IconButton>
          )}
          <Typography variant="h6" component="span" sx={{ fontWeight: 700, flexGrow: 1 }}>
            iTrade
          </Typography>
          <ModeBadge mode={status.mode} />
          <StatusChip name="Account" value={status.account} />
          <StatusChip name="Reconciliation" value={status.reconciliation} />
          <ThemeToggle />
        </Toolbar>
      </AppBar>

      <Box component="nav" aria-label="Main" sx={{ width: wide ? drawerWidth : 0, flexShrink: 0 }}>
        <Drawer
          variant={wide ? 'permanent' : 'temporary'}
          open={wide || menuOpen}
          onClose={() => {
            setMenuOpen(false)
          }}
          sx={{ '& .MuiDrawer-paper': { width: drawerWidth, boxSizing: 'border-box' } }}
        >
          {menu}
        </Drawer>
      </Box>

      <Box component="main" sx={{ flexGrow: 1, minWidth: 0, p: 3 }}>
        <Toolbar />
        {children}
      </Box>
    </Box>
  )
}

/** PAPER and LIVE must never be confused: LIVE is red, PAPER amber, unknown is spelled out. */
function ModeBadge({ mode }: { mode: TradingMode | undefined }) {
  if (mode === undefined) {
    return (
      <Chip
        label="NOT CONNECTED"
        variant="outlined"
        size="small"
        aria-label="Trading mode: not connected"
      />
    )
  }

  return (
    <Chip
      label={mode.toUpperCase()}
      color={mode === 'Live' ? 'error' : 'warning'}
      size="small"
      aria-label={`Trading mode: ${mode}`}
      sx={{ fontWeight: 700, letterSpacing: 0.5 }}
    />
  )
}

function StatusChip({ name, value }: { name: string; value: string | undefined }) {
  return (
    <Chip
      label={`${name}: ${value ?? '—'}`}
      variant="outlined"
      size="small"
      sx={{ display: { xs: 'none', sm: 'inline-flex' } }}
    />
  )
}

function ThemeToggle() {
  const { mode, systemMode, setMode } = useColorScheme()
  const current = mode === 'system' ? systemMode : mode
  if (current === undefined) {
    return null
  }

  const next = current === 'dark' ? 'light' : 'dark'
  const label = `Switch to ${next} theme`
  return (
    <Tooltip title={label}>
      <IconButton
        edge="end"
        aria-label={label}
        onClick={() => {
          setMode(next)
        }}
      >
        {next === 'dark' ? <DarkModeOutlined /> : <LightModeOutlined />}
      </IconButton>
    </Tooltip>
  )
}
