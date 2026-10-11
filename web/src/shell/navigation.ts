import AddCardOutlined from '@mui/icons-material/AddCardOutlined'
import AssessmentOutlined from '@mui/icons-material/AssessmentOutlined'
import ReceiptLongOutlined from '@mui/icons-material/ReceiptLongOutlined'
import SettingsOutlined from '@mui/icons-material/SettingsOutlined'
import SpaceDashboardOutlined from '@mui/icons-material/SpaceDashboardOutlined'
import TableRowsOutlined from '@mui/icons-material/TableRowsOutlined'
import type { SvgIconComponent } from '@mui/icons-material'

export interface NavigationItem {
  path: string
  label: string
  icon: SvgIconComponent
}

/** The six screens of the platform (PLAN §3, module Web), in menu order. */
export const navigation: readonly NavigationItem[] = [
  { path: '/', label: 'Overview', icon: SpaceDashboardOutlined },
  { path: '/positions', label: 'Positions', icon: TableRowsOutlined },
  { path: '/contribute', label: 'Contribute', icon: AddCardOutlined },
  { path: '/taxes', label: 'Taxes', icon: ReceiptLongOutlined },
  { path: '/reports', label: 'Reports', icon: AssessmentOutlined },
  { path: '/settings', label: 'Settings', icon: SettingsOutlined },
]
