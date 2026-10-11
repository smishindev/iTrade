import Typography from '@mui/material/Typography'

interface PlaceholderPageProps {
  title: string
  description: string
}

/** A screen that exists in the menu but is built in a later phase. */
export function PlaceholderPage({ title, description }: PlaceholderPageProps) {
  return (
    <>
      <Typography variant="h4" component="h1" gutterBottom>
        {title}
      </Typography>
      <Typography sx={{ color: 'text.secondary' }}>{description}</Typography>
    </>
  )
}
