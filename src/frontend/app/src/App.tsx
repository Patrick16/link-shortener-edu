import { BrowserRouter, Link, Navigate, Route, Routes } from 'react-router-dom'
import { AuthProvider, useAuth } from './auth/AuthContext'
import CreateLinkPage from './pages/CreateLinkPage'
import LoginPage from './pages/LoginPage'
import './App.css'

function NavBar() {
  const { user, logout } = useAuth()

  return (
    <header className="nav-bar">
      <Link to="/" className="brand">
        Link Shortener
      </Link>
      <div className="nav-actions">
        {user ? (
          <>
            <span className="nav-user">{user.email}</span>
            <button type="button" onClick={logout}>
              Sign out
            </button>
          </>
        ) : (
          <Link to="/login">Sign in</Link>
        )}
      </div>
    </header>
  )
}

function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        <NavBar />
        <main>
          <Routes>
            <Route path="/" element={<CreateLinkPage />} />
            <Route path="/login" element={<LoginPage />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </main>
      </BrowserRouter>
    </AuthProvider>
  )
}

export default App
